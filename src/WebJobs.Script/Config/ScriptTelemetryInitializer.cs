﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.Azure.WebJobs.Script.Workers.Rpc;
using Microsoft.Extensions.Options;

namespace Microsoft.Azure.WebJobs.Script.Config
{
    internal class ScriptTelemetryInitializer : ITelemetryInitializer
    {
        private readonly ScriptJobHostOptions _hostOptions;

        public ScriptTelemetryInitializer(IOptions<ScriptJobHostOptions> hostOptions)
        {
            if (hostOptions == null)
            {
                throw new ArgumentNullException(nameof(hostOptions));
            }

            if (hostOptions.Value == null)
            {
                throw new ArgumentNullException(nameof(hostOptions.Value));
            }

            _hostOptions = hostOptions.Value;
        }

        public void Initialize(ITelemetry telemetry)
        {
            IDictionary<string, string> telemetryProps = telemetry?.Context?.Properties;

            if (telemetryProps == null)
            {
                return;
            }

            telemetryProps[ScriptConstants.LogPropertyHostInstanceIdKey] = _hostOptions.InstanceId;

            if (telemetry is ExceptionTelemetry exceptionTelemetry && exceptionTelemetry.Exception.InnerException is RpcException rpcException
                && rpcException.IsUserException && FeatureFlags.IsEnabled(ScriptConstants.FeatureFlagEnableUserException))
            {
                exceptionTelemetry.Message = rpcException.RemoteMessage;

                // TODO - remove. For testing purposes while worker changes aren't in place yet.
                rpcException.RemoteTypeName = "test SetParsedStack";

                string typeName = string.IsNullOrEmpty(rpcException.RemoteTypeName) ? rpcException.GetType().ToString() : rpcException.RemoteTypeName;
                var detailsInfoItem = exceptionTelemetry.ExceptionDetailsInfoList.FirstOrDefault(s => s.TypeName.Contains("RpcException"));
                exceptionTelemetry.ExceptionDetailsInfoList.FirstOrDefault(s => s.TypeName.Contains("RpcException")).TypeName = typeName;

                Exception ex = exceptionTelemetry.Exception.InnerException;
                Exception aex = new Exception();
                StackTrace st = new StackTrace(aex, true);


                var sf = new System.Diagnostics.StackFrame();
                System.Diagnostics.StackFrame[] sfa = new System.Diagnostics.StackFrame[] { sf };
                exceptionTelemetry.SetParsedStack(sfa);
            }
        }
    }
}
