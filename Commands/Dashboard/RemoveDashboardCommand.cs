// Copyright (c) 2021 Snowflake Inc. All rights reserved.

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
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management.Automation;

namespace Snowflake.Powershell
{
    [Cmdlet
        (VerbsCommon.Remove,
        "SFDashboard",
        DefaultParameterSetName="DashboardName",
        SupportsPaging=false,
        SupportsShouldProcess=false)]
    [OutputType(typeof(String))]
    public class RemoveDashboardCommand : PSCmdlet
    {
        private static Logger logger = LogManager.GetCurrentClassLogger();
        private static Logger loggerConsole = LogManager.GetLogger("Snowflake.Powershell.Console");

        Stopwatch stopWatch = new Stopwatch();

        [Parameter(
            Mandatory = true,
            Position = 0,
            ValueFromPipeline = true,
            ValueFromPipelineByPropertyName = true,
            HelpMessage = "Application user context from authentication process")]
        public AppUserContext AuthContext { get; set; }

        [Parameter(
            Mandatory = true,
            Position = 1,
            ValueFromPipeline = true,
            ValueFromPipelineByPropertyName = true,
            HelpMessage = "Name of Dashboard to delete",
            ParameterSetName = "DashboardName")]
        public string DashboardName { get; set; }

        [Parameter(
            Mandatory = true,
            Position = 1,
            ValueFromPipeline = true,
            ValueFromPipelineByPropertyName = true,
            HelpMessage = "ID of Dashboard to delete",
            ParameterSetName = "DashboardID")]
        public string DashboardID { get; set; }

        [Parameter(
            Mandatory = true,
            Position = 1,
            ValueFromPipeline = true,
            ValueFromPipelineByPropertyName = true,
            HelpMessage = "Dashboard object of Dashboard to delete",
            ParameterSetName = "DashboardObject")]
        public Dashboard Dashboard { get; set; }

        [Parameter(
            Mandatory = true,
            Position = 1,
            ValueFromPipeline = true,
            ValueFromPipelineByPropertyName = true,
            HelpMessage = "File representation of Dashboard to delete",
            ParameterSetName = "DashboardFile")]
        public string DashboardFile { get; set; }

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
                logger.Info("ParameterSetName={0}", this.ParameterSetName);

                switch (this.ParameterSetName)
                {                 
                    case "DashboardObject":
                        break;

                    case "DashboardFile":
                        if (File.Exists(this.DashboardFile) == false)
                        {
                            throw new FileNotFoundException(String.Format("No Dashboard file found at {0}", this.DashboardFile));
                        }

                        this.Dashboard = JsonConvert.DeserializeObject<Dashboard>(FileIOHelper.ReadFileFromPath(this.DashboardFile));
                        if (this.Dashboard == null)
                        {
                            throw new ArgumentNullException(String.Format("Unable to convert file found at {0} to Dashboard", this.DashboardFile));
                        }

                        break;

                    case "DashboardName":
                    case "DashboardID":
                        break;

                    default:
                        throw new ArgumentException(String.Format("Unknown parameter set {0}", this.ParameterSetName));
                }

                // Get all dashboards already present
                string dashboardsApiResult = SnowflakeDriver.GetDashboards(this.AuthContext.AppServerUrl, this.AuthContext.AccountUrl, this.AuthContext.OrganizationID, this.AuthContext.UserName, this.AuthContext.AuthTokenSnowsight);
                if (dashboardsApiResult.Length == 0)
                {
                    throw new ItemNotFoundException("Invalid response from listing dashboard entities");
                }

                JObject dashboardsPayloadObject = JObject.Parse(dashboardsApiResult);

                JArray entitiesArray = new JArray();
                if (JSONHelper.isTokenPropertyNull(dashboardsPayloadObject, "entities") == false)
                {
                    entitiesArray = (JArray)dashboardsPayloadObject["entities"];
                }
                logger.Info("Number of Entities={0}", entitiesArray.Count);

                List<Dashboard> dashboardsToDeleteList = new List<Dashboard>(entitiesArray.Count);

                foreach (JObject entityObject in entitiesArray)
                {
                    // Only deal with "folder" objects, which are dashboards
                    if (JSONHelper.getStringValueFromJToken(entityObject, "entityType") != "folder") continue;

                    Dashboard potentialTargetDashboard = new Dashboard(entityObject, dashboardsPayloadObject, this.AuthContext);

                    switch (this.ParameterSetName)
                    {                 
                        case "DashboardName":
                            if (this.DashboardName == potentialTargetDashboard.DashboardName) 
                            {
                                logger.Info("Found Match by Name: {0}={1}", this.DashboardName, potentialTargetDashboard);
                                
                                dashboardsToDeleteList.Add(potentialTargetDashboard);
                            }
                            break;

                        case "DashboardID":                            
                            if (this.DashboardID == potentialTargetDashboard.DashboardID) 
                            {
                                logger.Info("Found Match by ID: {0}={1}", this.DashboardID, potentialTargetDashboard);

                                dashboardsToDeleteList.Add(potentialTargetDashboard);
                            }
                            break;

                        case "WorksheetFile":
                        case "WorksheetObject":
                            if (this.Dashboard.DashboardID == potentialTargetDashboard.DashboardID) 
                            {
                                logger.Info("Found Match by ID: {0}={1}", this.Dashboard.DashboardID, potentialTargetDashboard);

                                dashboardsToDeleteList.Add(potentialTargetDashboard);
                            }
                            else if (String.Compare(this.Dashboard.DashboardName, potentialTargetDashboard.DashboardName, true) == 0) 
                            {
                                logger.Info("Found Match by Name: {0}={1}", this.Dashboard.DashboardName, potentialTargetDashboard);
                                
                                dashboardsToDeleteList.Add(potentialTargetDashboard);
                            }
                            break;

                        default:
                            throw new ArgumentException(String.Format("Unknown parameter set {0}", this.ParameterSetName));
                    }
                }

                logger.Info("Number of Dashboards to Delete={0}", dashboardsToDeleteList.Count);
                loggerConsole.Info("Deleting {0} Dashboards", dashboardsToDeleteList.Count);

                foreach (Dashboard dashboard in dashboardsToDeleteList)
                {
                    logger.Info("Deleting {0}", dashboard);
                    loggerConsole.Trace("Deleting Dashboard {0} ({1})", dashboard.DashboardName, dashboard.DashboardID);
                    
                    // Delete the Worksheet
                    string dashboardDeleteApiResult = SnowflakeDriver.DeleteDashboard(this.AuthContext.AppServerUrl, this.AuthContext.AccountUrl, this.AuthContext.UserName, this.AuthContext.AuthTokenSnowsight, dashboard.DashboardID);
                    if (dashboardDeleteApiResult.Length == 0)
                    {
                        throw new ItemNotFoundException("Invalid response from deleting dashboard entity");
                    }
                }

                WriteObject(String.Format("Deleted {0} objects", dashboardsToDeleteList.Count));
            }
            catch (Exception ex)
            {
                logger.Error("{0} threw {1} ({2})", this.GetType().Name, ex.Message, ex.Source);
                logger.Error(ex);

                if (ex is ItemNotFoundException)
                {
                    this.ThrowTerminatingError(new ErrorRecord(ex, "errorid", ErrorCategory.ObjectNotFound, null));
                }
                else if (ex is FileNotFoundException)
                {
                    this.ThrowTerminatingError(new ErrorRecord(ex, "errorid", ErrorCategory.ObjectNotFound, null));
                }
                else if (ex is ArgumentNullException)
                {
                    this.ThrowTerminatingError(new ErrorRecord(ex, "errorid", ErrorCategory.ObjectNotFound, null));
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
    }
}
