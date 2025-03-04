﻿// Copyright (c) 2021 Snowflake Inc. All rights reserved.

// Licensed under the Apache License, Version 2.0 (the
// "License"); you may not use this file except in compliance
// with the License.  You may obtain a copy of the License at

//   http://www.apache.org/licenses/LICENSE-2.0

// Unless required by applicable law or agreed to in writing,
// software distributed under the License is distributed on an
// "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY
// KIND, either express or implied.  See the License for the
// specific language governing permissions and limitations
// under the License.

using NLog;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Management.Automation;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Authentication;
using System.Text.RegularExpressions;
using System.Web;


namespace Snowflake.Powershell
{
    [Cmdlet
        (VerbsCommunications.Connect,
        "SFApp",
        DefaultParameterSetName="UserNamePasswordString",
        SupportsPaging=false,
        SupportsShouldProcess=false)]
    [OutputType(typeof(String))]
    public class ConnectAppCommand : PSCmdlet
    {
        private static Logger logger = LogManager.GetCurrentClassLogger();
        private static Logger loggerConsole = LogManager.GetLogger("Snowflake.Powershell.Console");

        private static readonly string TOKEN_REQUEST_PREFIX = "?token=";
        private static readonly byte[] SUCCESS_RESPONSE = System.Text.Encoding.UTF8.GetBytes(
            "<!DOCTYPE html><html><head><meta charset=\"UTF-8\"/>" +
            "<title> SAML Response for Snowflake </title></head>" +
            "<body>Your identity was confirmed and propagated to Snowflake Powershell. You can close this window now and go back to where you started from." +
            "</body></html>;"
            );
            
        Stopwatch stopWatch = new Stopwatch();

        [Parameter(
            Mandatory = true,
            Position = 0,
            ValueFromPipeline = true,
            ValueFromPipelineByPropertyName = true,
            HelpMessage = "Account to access",
            ParameterSetName = "UserNamePasswordString")]
        [Parameter(
            Mandatory = true,
            Position = 0,
            ValueFromPipeline = true,
            ValueFromPipelineByPropertyName = true,
            HelpMessage = "Account to access",
            ParameterSetName = "UserNamePasswordPSCredential")]
        [Parameter(
            Mandatory = true,
            Position = 0,
            ValueFromPipeline = true,
            ValueFromPipelineByPropertyName = true,
            HelpMessage = "Account to access",
            ParameterSetName = "BrowserSSO")]
        public string Account { get; set; }

        [Parameter(
            Mandatory = true,
            Position = 1,
            ValueFromPipeline = true,
            ValueFromPipelineByPropertyName = true,
            HelpMessage = "Username with which to access account",
            ParameterSetName = "UserNamePasswordString")]
        [Parameter(
            Mandatory = true,
            Position = 1,
            ValueFromPipeline = true,
            ValueFromPipelineByPropertyName = true,
            HelpMessage = "Username with which to access account",
            ParameterSetName = "BrowserSSO")]
        public string UserName { get; set; }

        [Parameter(
            Mandatory = true,
            Position = 2,
            ValueFromPipeline = true,
            ValueFromPipelineByPropertyName = true,
            HelpMessage = "Password with which to authenticate",
            ParameterSetName = "UserNamePasswordString")]
        public SecureString Password { get; set; }

        [Parameter(
            Mandatory = true,
            Position = 1,
            ValueFromPipelineByPropertyName = true,
            HelpMessage = "Username and password in Credentials object with which to authenticate",
            ParameterSetName = "UserNamePasswordPSCredential")]
        [Credential]
        public PSCredential Credential { get; set; }

        [Parameter(
            Mandatory = true,
            Position = 3,
            ValueFromPipeline = true,
            ValueFromPipelineByPropertyName = true,
            HelpMessage = "Use Browser to authenticate with SSO provider",
            ParameterSetName = "BrowserSSO")]
        public SwitchParameter SSO { get; set; }

        protected override void BeginProcessing()
        {
            stopWatch.Start();

            NLogHelper.ConfigureNLog();

            logger = LogManager.GetCurrentClassLogger();
            loggerConsole = LogManager.GetLogger("Snowflake.Powershell.Console");

            logger.Trace("BEGIN {0}", this.GetType().Name);
            WriteVerbose(String.Format("BEGIN {0}", this.GetType().Name));
        }

        protected override void EndProcessing()
        {
            stopWatch.Stop();

            logger.Trace("END {0} execution took {1:c} ({2} ms)", this.GetType().Name, stopWatch.Elapsed, stopWatch.ElapsedMilliseconds);
            loggerConsole.Trace("Execution took {0:c} ({1} ms)", stopWatch.Elapsed, stopWatch.ElapsedMilliseconds);            
            WriteVerbose(String.Format("END {0}, execution took {1:c} ({2} ms)", this.GetType().Name, stopWatch.Elapsed, stopWatch.ElapsedMilliseconds));
            
            LogManager.Flush();
        }

        protected override void ProcessRecord()
        {
            try
            {
                AppUserContext appUserContext = new AppUserContext();               

                #region Parameter set validation

                // Get credentials
                logger.Info("ParameterSetName={0}", this.ParameterSetName);
                switch (this.ParameterSetName)
                {
                    case "UserNamePasswordString":
                        break;

                    case "UserNamePasswordPSCredential":
                        this.UserName = this.Credential.UserName;
                        this.Password = this.Credential.Password;

                        break;

                    case "BrowserSSO":
                        break;

                    default:
                        throw new ArgumentException(String.Format("Unknown parameter set {0}", this.ParameterSetName));
                }
                appUserContext.UserName = this.UserName;
                logger.Info("Username={0}", appUserContext.UserName);

                #endregion

                #region Account and region validation

                // Get authentication endpoint for the account 
                string accountEndpointResult = SnowflakeDriver.GetAccountAppEndpoints(this.Account);
                if (accountEndpointResult.Length == 0)
                {
                    throw new ItemNotFoundException(String.Format("Unable to get account endpoint for account {0}", this.Account));
                }

                // Is the account valid?
                JObject accountAuthenticationEndpointObject = JObject.Parse(accountEndpointResult);
                if (JSONHelper.getBoolValueFromJToken(accountAuthenticationEndpointObject, "valid") == false)
                {                    
                    // {"valid":false}
                    throw new ItemNotFoundException(String.Format("No valid account endpoint for account {0}", this.Account));
                }

                // {
                //     "account": "aws_cas1",
                //     "appServerUrl": "https://apps-api.c1.us-west-2.aws.app.snowflake.com",
                //     "region": "us-west-2",
                //     "url": "https://aws_cas1.snowflakecomputing.com",
                //     "valid": true
                // }
                // {
                //     "account": "sfpscogs_dodievich_sso",
                //     "appServerUrl": "https://apps-api.c1.westus2.azure.app.snowflake.com",
                //     "region": "west-us-2.azure",
                //     "url": "https://sfpscogs_dodievich_sso.west-us-2.azure.snowflakecomputing.com",
                //     "valid": true
                // }
                appUserContext.AppServerUrl = JSONHelper.getStringValueFromJToken(accountAuthenticationEndpointObject, "appServerUrl");
                appUserContext.AccountUrl = JSONHelper.getStringValueFromJToken(accountAuthenticationEndpointObject, "url");
                appUserContext.Region = JSONHelper.getStringValueFromJToken(accountAuthenticationEndpointObject, "region");
                // sfpscogs_dodievich_sso.west-us-2.azure -> sfpscogs_dodievich_sso
                appUserContext.AccountFullName = this.Account;
                appUserContext.AccountName = appUserContext.AccountFullName.Split('.')[0];
                logger.Info("AccountFullName={0}", appUserContext.AccountFullName);
                logger.Info("AccountName={0}", appUserContext.AccountName);
                logger.Info("AccountUrl={0}", appUserContext.AccountUrl);
                logger.Info("AppServerUrl={0}", appUserContext.AppServerUrl);
                logger.Info("Region={0}", appUserContext.Region);

                loggerConsole.Trace("Account '{0}' in region '{1}' is accessible at '{2}' and served by application server '{3}'", appUserContext.AccountName, appUserContext.Region, appUserContext.AccountUrl, appUserContext.AppServerUrl);

                #endregion

                #region Snowsight Client ID 

                // Get the client ID of Snowsight for this region
                string deploymentSnowSightClientIDRedirectResult = SnowflakeDriver.GetSnowSightClientIDInDeployment(appUserContext.AppServerUrl, appUserContext.AccountUrl);
                if (deploymentSnowSightClientIDRedirectResult.Length == 0)
                {
                    throw new ItemNotFoundException(String.Format("Unable to get account client ID for account {0}", appUserContext.AccountName));
                }

                string redirectWithClientIdUrl = String.Empty;
                Regex regexVersion = new Regex(@"<a href=""(.*)"">Found<\/a>", RegexOptions.IgnoreCase);
                Match match = regexVersion.Match(deploymentSnowSightClientIDRedirectResult);
                if (match != null)
                {
                    if (match.Groups.Count > 1)
                    {
                        redirectWithClientIdUrl = match.Groups[1].Value;
                    }
                }

                if (redirectWithClientIdUrl.Length == 0)
                {
                    throw new ItemNotFoundException(String.Format("Unable to parse URL with client ID for account {0}", appUserContext.AccountName));
                }

                Uri redirectWithClientIdUri = new Uri(redirectWithClientIdUrl);
                NameValueCollection redirectWithClientIdParams = HttpUtility.ParseQueryString(redirectWithClientIdUri.Query);
                appUserContext.ClientID = redirectWithClientIdParams["client_id"];
                if (appUserContext.ClientID == null)
                {
                    throw new ItemNotFoundException(String.Format("Unable to parse client ID from URL with client ID for account {0}", appUserContext.AccountName));
                }
                
                // OAuth Client ID of the SnowSight is different for each deployment
                // PROD1    ClientID=R/ykyhaxXg8WlftPZd6Ih0Y4auOsVg== 
                // AZWEST2  ClientID=uGWIv9zROgdvkWNlFHo4zi+F1M2joA==
                logger.Info("ClientID={0}", appUserContext.ClientID);

                #endregion

                #region Classic UI Authentication

                loggerConsole.Info("Authenticating user {0} in account {1} to Classic UI", appUserContext.UserName, appUserContext.AccountName);

                if (this.SSO.IsPresent == false)
                {
                    // Authenticate with username/password
                    string masterTokenAndSessionTokenFromCredentialsResult = SnowflakeDriver.GetMasterTokenAndSessionTokenFromCredentials(appUserContext.AccountUrl, appUserContext.AccountName, appUserContext.UserName, new System.Net.NetworkCredential(string.Empty, this.Password).Password);
                    if (masterTokenAndSessionTokenFromCredentialsResult.Length == 0)
                    {
                        throw new InvalidCredentialException(String.Format("Invalid response on authenticate user request {0}@{1}", appUserContext.UserName, appUserContext.AccountName));
                    }

                    // Were the credentials good?
                    JObject masterTokenAndSessionTokenFromCredentialsObject = JObject.Parse(masterTokenAndSessionTokenFromCredentialsResult);
                    if (JSONHelper.getBoolValueFromJToken(masterTokenAndSessionTokenFromCredentialsObject, "success") == false)
                    {                    
                        // {
                        //     "data": {
                        //         "nextAction": "RETRY_LOGIN",
                        //         "pwdChangeInfo": null,
                        //         "inFlightCtx": null,
                        //         "redirectUrl": null,
                        //         "licenseAgreementPDFFilePath": null,
                        //         "licenseAgreementHTMLFilePath": null,
                        //         "authnMethod": "USERNAME_PASSWORD",
                        //         "oAuthSessionStorageData": null,
                        //         "relayState": null
                        //     },
                        //     "code": "390100",
                        //     "message": "Incorrect username or password was specified.",
                        //     "success": false,
                        //     "headers": null
                        // }
                        throw new InvalidCredentialException(String.Format("Unable to authenticate user {0}@{1} because of {2} ({3})", appUserContext.UserName, appUserContext.AccountName, JSONHelper.getStringValueFromJToken(masterTokenAndSessionTokenFromCredentialsObject, "message"), JSONHelper.getStringValueFromJToken(masterTokenAndSessionTokenFromCredentialsObject, "code")));
                    }

                    // If we got here, we have good credentials and first step is good
                    appUserContext.AuthTokenMaster = JSONHelper.getStringValueFromJToken(masterTokenAndSessionTokenFromCredentialsObject["data"], "masterToken");
                    if (appUserContext.AuthTokenMaster.Length == 0)
                    {
                        throw new InvalidCredentialException(String.Format("No master token on authenticate user request {0}@{1}", appUserContext.UserName, appUserContext.AccountName));
                    }
                    logger.Info("AuthTokenMaster={0}", appUserContext.AuthTokenMaster);

                    appUserContext.AuthTokenSession = JSONHelper.getStringValueFromJToken(masterTokenAndSessionTokenFromCredentialsObject["data"], "token");
                    if (appUserContext.AuthTokenSession.Length == 0)
                    {
                        throw new InvalidCredentialException(String.Format("No session token on authenticate user request {0}@{1}", appUserContext.UserName, appUserContext.AccountName));
                    }
                    logger.Info("AuthTokenSession={0}", appUserContext.AuthTokenSession);

                    appUserContext.ServerVersion = JSONHelper.getStringValueFromJToken(masterTokenAndSessionTokenFromCredentialsObject["data"], "serverVersion");
                    logger.Info("ServerVersion={0}", appUserContext.ServerVersion);
                }
                else
                {
                    // Authenticate with SSO
                    int localPort = GetRandomUnusedPort();

                    string ssoLoginLinkResult = SnowflakeDriver.GetSSOLoginLinkForAccountAndUser(appUserContext.AccountUrl, appUserContext.AccountName, appUserContext.UserName, localPort);
                    if (ssoLoginLinkResult.Length == 0)
                    {
                        throw new InvalidCredentialException(String.Format("Invalid response on getting SSO for user {0}@{1}", appUserContext.UserName, appUserContext.AccountName));
                    }

                    JObject ssoLoginLinkObject = JObject.Parse(ssoLoginLinkResult);
                    if (JSONHelper.getBoolValueFromJToken(ssoLoginLinkObject, "success") == false)
                    {                    
                        // {
                        //     "data": {
                        //         "tokenUrl": null,
                        //         "ssoUrl": "https://snowbiz.okta.com/app/snowflake/exk8wfsfryJIn4IWZ2p7/sso/saml?SAMLRequest=jVJdc9owEPwrHvXZlmzSlmiADIVk4k4CFJuQ5k3YMmiQJVcnx8Cvr8xHJ31IJm%2Ba0%2B7t3u31bnal9F65AaFVH4UBQR5Xmc6FWvfRIr3zu8gDy1TOpFa8j%2FYc0M2gB6yUFR3WdqPm%2FE%2FNwXqukQLafvRRbRTVDARQxUoO1GY0GT4%2B0CgglAFwY50cOlNyEE5rY21FMW6aJmg6gTZrHBFCMLnGDtVCvqA3EtXHGpXRVmdaXig7N9M7EiEmV62EQziF2Zn4Q6jTCj5SWZ1AQO%2FTdObPpkmKvOFlupFWUJfcJNy8iowv5g8nA%2BAcJJPp8n66SG4DULopJNvyTJdVbV23wL1wwXMs9Vq4HcXjPqq2Irf6uqim6XpNDnI7Xx34787mwLPFbTw0z4%2BT0fI5AcPiX6u9zpD3dEk0ahONAWoeqzZH60okCn3y1Sff0iikYYd2oqDbIS%2FIG7schWL2yLyYbS2uxCHQW8uO5lhV4X%2B%2BMd9tu00Bhdn%2FjNVVvHyJqu8YQOM2JnS6FHo0YAafnb%2BH37LOxzZx%2B4%2FHMy1FtvfutCmZfT%2BeMAiPFZH7xRFKecmEHOa54QAuJil1MzKcWXfT1tQc4cFJ9f%2BrHvwF&RelayState=12345",
                        //         "proofKey": "PEJqsVhc0y6mWq9SN/ESmuCRZKZs0/WgdkBRxZr75Yg=_-1_0"
                        //     },
                        //     "code": null,
                        //     "message": null,
                        //     "success": true
                        // }
                        throw new InvalidCredentialException(String.Format("Unable to authenticate user {0}@{1} because of {2} ({3})", appUserContext.UserName, appUserContext.AccountName, JSONHelper.getStringValueFromJToken(ssoLoginLinkObject, "message"), JSONHelper.getStringValueFromJToken(ssoLoginLinkObject, "code")));
                    }

                    // If we got here, we have good SSO link
                    string idpUrl = JSONHelper.getStringValueFromJToken(ssoLoginLinkObject["data"], "ssoUrl");
                    string proofKey = JSONHelper.getStringValueFromJToken(ssoLoginLinkObject["data"], "proofKey");
                    logger.Info("Proof Key={0}", proofKey);

                    string samlResponseToken = String.Empty;

                    // Open browser to SSO
                    using (var httpListener = GetHttpListener(localPort))
                    {
                        httpListener.Start();

                        logger.Info("Opening SSO URL={0}", idpUrl);
                        StartBrowser(idpUrl);

                        logger.Info("Get the redirect SAML request");
                        var context = httpListener.GetContext();
                        var request = context.Request;
                        samlResponseToken = ValidateAndExtractToken(request);
                        logger.Info("SAML Token={0}", samlResponseToken);
                        
                        HttpListenerResponse response = context.Response;
                        try
                        {
                            using (var output = response.OutputStream)
                            {
                                output.Write(SUCCESS_RESPONSE, 0, SUCCESS_RESPONSE.Length);
                            }
                        }
                        catch (Exception ex)
                        {
                            // Ignore the exception as it does not affect the overall authentication flow
                            logger.Warn("External authentication browser response not sent out");
                            logger.Warn(ex);
                        }

                        httpListener.Stop();
                    }

                    // Once we got here, we should have our token received our response
                    if (samlResponseToken.Length == 0)
                    {
                        throw new InvalidCredentialException(String.Format("Unable to get SSO SAML response token for user {0}@{1}", appUserContext.UserName, appUserContext.AccountName));
                    }

                    // Complete SSO from IdP Token into the session
                    string masterTokenAndSessionTokenFromSSOTokenResult = SnowflakeDriver.GetMasterTokenAndSessionTokenFromSSOToken(appUserContext.AccountUrl, appUserContext.AccountName, appUserContext.UserName, samlResponseToken, proofKey);
                    if (masterTokenAndSessionTokenFromSSOTokenResult.Length == 0)
                    {
                        throw new InvalidCredentialException(String.Format("Invalid response on authenticate user request {0}@{1}", appUserContext.UserName, appUserContext.AccountName));
                    }

                    // Were the credentials good?
                    JObject masterTokenAndSessionTokenFromSSOTokenObject = JObject.Parse(masterTokenAndSessionTokenFromSSOTokenResult);
                    if (JSONHelper.getBoolValueFromJToken(masterTokenAndSessionTokenFromSSOTokenObject, "success") == false)
                    {                    
                        throw new InvalidCredentialException(String.Format("Unable to authenticate user {0}@{1} because of {2} ({3})", appUserContext.UserName, appUserContext.AccountName, JSONHelper.getStringValueFromJToken(masterTokenAndSessionTokenFromSSOTokenObject, "message"), JSONHelper.getStringValueFromJToken(masterTokenAndSessionTokenFromSSOTokenObject, "code")));
                    }

                    // If we got here, we have good credentials and first step is good
                    appUserContext.AuthTokenMaster = JSONHelper.getStringValueFromJToken(masterTokenAndSessionTokenFromSSOTokenObject["data"], "masterToken");
                    if (appUserContext.AuthTokenMaster.Length == 0)
                    {
                        throw new InvalidCredentialException(String.Format("No master token on authenticate user request {0}@{1}", appUserContext.UserName, appUserContext.AccountName));
                    }
                    logger.Info("AuthTokenMaster={0}", appUserContext.AuthTokenMaster);

                    appUserContext.AuthTokenSession = JSONHelper.getStringValueFromJToken(masterTokenAndSessionTokenFromSSOTokenObject["data"], "token");
                    if (appUserContext.AuthTokenSession.Length == 0)
                    {
                        throw new InvalidCredentialException(String.Format("No session token on authenticate user request {0}@{1}", appUserContext.UserName, appUserContext.AccountName));
                    }
                    logger.Info("AuthTokenSession={0}", appUserContext.AuthTokenSession);

                    appUserContext.ServerVersion = JSONHelper.getStringValueFromJToken(masterTokenAndSessionTokenFromSSOTokenObject["data"], "serverVersion");
                    logger.Info("ServerVersion={0}", appUserContext.ServerVersion);
                }

                #endregion

                #region Snowsight Authentication

                // Authenticate to Snowsight second, using APIs we've implemented

                loggerConsole.Info("Authenticating user {0} in account {1} to Snowsight", appUserContext.UserName, appUserContext.AccountName);

                if (this.SSO.IsPresent == false)
                {
                    // Authenticate with username/password

                    // Authenticate Step 1, Getting OAuth Token
                    string masterTokenFromCredentialsResult = SnowflakeDriver.GetMasterTokenFromCredentials(appUserContext.AccountUrl, appUserContext.AccountName, appUserContext.UserName, new System.Net.NetworkCredential(string.Empty, this.Password).Password);
                    if (masterTokenFromCredentialsResult.Length == 0)
                    {
                        throw new InvalidCredentialException(String.Format("Invalid response on authenticate user request {0}@{1}", appUserContext.UserName, appUserContext.AccountName));
                    }

                    // Were the credentials good?
                    JObject masterTokenFromCredentialsObject = JObject.Parse(masterTokenFromCredentialsResult);
                    if (JSONHelper.getBoolValueFromJToken(masterTokenFromCredentialsObject, "success") == false)
                    {                    
                        // {
                        //     "data": {
                        //         "nextAction": "RETRY_LOGIN",
                        //         "pwdChangeInfo": null,
                        //         "inFlightCtx": null,
                        //         "redirectUrl": null,
                        //         "licenseAgreementPDFFilePath": null,
                        //         "licenseAgreementHTMLFilePath": null,
                        //         "authnMethod": "USERNAME_PASSWORD",
                        //         "oAuthSessionStorageData": null,
                        //         "relayState": null
                        //     },
                        //     "code": "390100",
                        //     "message": "Incorrect username or password was specified.",
                        //     "success": false,
                        //     "headers": null
                        // }
                        throw new InvalidCredentialException(String.Format("Unable to authenticate user {0}@{1} because of {2} ({3})", appUserContext.UserName, appUserContext.AccountName, JSONHelper.getStringValueFromJToken(masterTokenFromCredentialsObject, "message"), JSONHelper.getStringValueFromJToken(masterTokenFromCredentialsObject, "code")));
                    }

                    // If we got here, we have good credentials and first step is good
                    appUserContext.AuthTokenMaster = JSONHelper.getStringValueFromJToken(masterTokenFromCredentialsObject["data"], "masterToken");
                    if (appUserContext.AuthTokenMaster.Length == 0)
                    {
                        throw new InvalidCredentialException(String.Format("No master token on authenticate user request {0}@{1}", appUserContext.UserName, appUserContext.AccountName));
                    }

                    logger.Info("AuthTokenMaster={0}", appUserContext.AuthTokenMaster);
                }
                else
                {
                    // Authenticate with SSO
                    
                    // Previous Classic UI authentication would have filled in masterToken, so nothing to do
                }

                loggerConsole.Info("Validating master token for user {0} in account {1}", appUserContext.UserName, appUserContext.AccountName);

                // Authenticate Step 2, Validating OAuth Token into OAuth Client Redirect
                string oAuthTokenFromMasterTokenResult = SnowflakeDriver.GetOAuthRedirectFromOAuthToken(appUserContext.AccountUrl, appUserContext.ClientID, appUserContext.AuthTokenMaster);
                if (oAuthTokenFromMasterTokenResult.Length == 0)
                {
                    throw new InvalidCredentialException(String.Format("Invalid response on validating master OAuth token for user {0}@{1}", appUserContext.UserName, appUserContext.AccountName));
                }

                // Were the credentials good?
                JObject oAuthTokenFromMasterTokenObject = JObject.Parse(oAuthTokenFromMasterTokenResult);
                if (String.Compare(JSONHelper.getStringValueFromJToken(oAuthTokenFromMasterTokenObject, "code"), "390302", true) == 0 || 
                    String.Compare(JSONHelper.getStringValueFromJToken(oAuthTokenFromMasterTokenObject, "message"), "Invalid consent request.", true) == 0)
                {                    
                    // {
                    //     "data": {
                    //         "nextAction": "OAUTH_INVALID",
                    //         "inFlightCtx": null
                    //     },
                    //     "code": "390302",
                    //     "message": "Invalid consent request.",
                    //     "success": false,
                    //     "headers": null
                    // }
                    throw new InvalidCredentialException(String.Format("Unable to validate user master OAuth token {0}@{1}, {2} ({3})", appUserContext.UserName, appUserContext.AccountName, JSONHelper.getStringValueFromJToken(oAuthTokenFromMasterTokenObject, "message"), JSONHelper.getStringValueFromJToken(oAuthTokenFromMasterTokenObject, "code")));
                }

                // if we got here, we have good credentials and the second step is good
                string redirectWithOAuthCodeUrl = JSONHelper.getStringValueFromJToken(oAuthTokenFromMasterTokenObject["data"], "redirectUrl");
                if (redirectWithOAuthCodeUrl.Length == 0)
                {
                    throw new ItemNotFoundException(String.Format("Unable to parse URL with OAuth Token for user {0}@{1}", appUserContext.UserName, appUserContext.AccountName));
                }

                Uri redirectWithOAuthCodeUri = new Uri(redirectWithOAuthCodeUrl);
                NameValueCollection redirectWithOAuthCodeParams = HttpUtility.ParseQueryString(redirectWithOAuthCodeUri.Query);
                string oAuthRedirectCode = redirectWithOAuthCodeParams["code"];
                if (oAuthRedirectCode == null)
                {
                    throw new ItemNotFoundException(String.Format("Unable to parse OAuth Token from URL with OAuth Token for user {0}@{1}", appUserContext.UserName, appUserContext.AccountName));
                }

                logger.Info("OAuth Redirect Code={0}", oAuthRedirectCode);

                loggerConsole.Info("Converting redirect token to authentication token for user {0} in account {1}", appUserContext.UserName, appUserContext.AccountName);

                // Authenticate Step 3, Converting OAuth redirect into authentication cookie
                appUserContext.AuthTokenSnowsight = SnowflakeDriver.GetAuthenticationTokenFromOAuthRedirectToken(appUserContext.AppServerUrl, appUserContext.AccountUrl, oAuthRedirectCode);
                if (appUserContext.AuthTokenSnowsight.Length == 0)
                {
                    throw new InvalidCredentialException(String.Format("Invalid response from completing redirect OAuth Token for user {0}@{1}", appUserContext.UserName, appUserContext.AccountName));
                }

                logger.Info("SnowsightAuthToken={0}", appUserContext.AuthTokenSnowsight);

                #endregion

                #region Organization and User ID context
                // Get Org ID and User ID for future use
                string organizationAndUserContextResult = SnowflakeDriver.GetOrganizationAndUserContext(appUserContext.AppServerUrl, appUserContext.AccountUrl, appUserContext.Region, appUserContext.AccountName, appUserContext.UserName, appUserContext.AuthTokenSnowsight);
                if (organizationAndUserContextResult.Length == 0)
                {
                    throw new ItemNotFoundException(String.Format("Invalid response from getting organization context for user {0}@{1}", appUserContext.UserName, appUserContext.AccountName));
                }

                JObject organizationAndUserContextObject = JObject.Parse(organizationAndUserContextResult);
                appUserContext.UserID = JSONHelper.getLongValueFromJToken(organizationAndUserContextObject["User"], "id").ToString();
                appUserContext.OrganizationID = JSONHelper.getStringValueFromJToken(organizationAndUserContextObject["Org"], "id");
                appUserContext.CSRFToken = JSONHelper.getStringValueFromJToken(organizationAndUserContextObject["PageParams"], "csrfToken");
                if (appUserContext.UserID.Length == 0 || appUserContext.OrganizationID.Length == 0)
                {
                    throw new ItemNotFoundException(String.Format("Unable to parse Organization and User Context for user {0}@{1}", appUserContext.UserName, appUserContext.AccountName));
                }

                logger.Info("UserID={0}", appUserContext.UserID);
                logger.Info("OrganizationID={0}", appUserContext.OrganizationID);
                logger.Info("CSRFToken={0}", appUserContext.CSRFToken);

                loggerConsole.Info("Successfully authenticated {0} ({1}) in account {2} ({3})", appUserContext.UserName, appUserContext.UserID, appUserContext.AccountName, appUserContext.OrganizationID);
               
                #endregion

                WriteObject(appUserContext);
            }
            catch (Exception ex)
            {
                logger.Error("{0} threw {1} ({2})", this.GetType().Name, ex.Message, ex.Source);
                logger.Error(ex);

                if (ex is ItemNotFoundException)
                {
                    this.ThrowTerminatingError(new ErrorRecord(ex, "errorid", ErrorCategory.ObjectNotFound, null));
                }
                else if (ex is InvalidCredentialException)
                {
                    this.ThrowTerminatingError(new ErrorRecord(ex, "errorid", ErrorCategory.SecurityError, null));
                }
                else
                {
                    this.ThrowTerminatingError(new ErrorRecord(ex, "errorid", ErrorCategory.OperationStopped, null));
                }
            }
            finally
            {
                LogManager.Flush();
            }
        }

        private static int GetRandomUnusedPort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private static HttpListener GetHttpListener(int port)
        {
            string redirectURI = string.Format("http://{0}:{1}/", IPAddress.Loopback, port);
            HttpListener listener = new HttpListener();
            listener.Prefixes.Add(redirectURI);
            listener.Prefixes.Add($"http://localhost:{port}/");
            return listener;
        }

        private static void StartBrowser(string url)
        {
            // hack because of this: https://github.com/dotnet/corefx/issues/10361
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Process.Start(new ProcessStartInfo("cmd", $"/c start {url.Replace("&", "^&")}") { CreateNoWindow = true });
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                Process.Start("xdg-open", url);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Process.Start("open", url);
            }
            else
            {
                throw new NotSupportedException("What platform are you on?");
            }
        }

        private static string ValidateAndExtractToken(HttpListenerRequest request)
        {
            if (request.HttpMethod != "GET")
            {
                throw new ArgumentException("BROWSER_RESPONSE_WRONG_METHOD", request.HttpMethod);
            }

            if (request.Url.Query == null || !request.Url.Query.StartsWith(TOKEN_REQUEST_PREFIX))
            {
                throw new ArgumentException("BROWSER_RESPONSE_INVALID_PREFIX", request.Url.Query);
            }

            return Uri.UnescapeDataString(request.Url.Query.Substring(TOKEN_REQUEST_PREFIX.Length));
        }        
    }
}