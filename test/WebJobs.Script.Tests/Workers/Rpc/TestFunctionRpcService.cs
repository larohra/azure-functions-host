﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs.Script.Description;
using Microsoft.Azure.WebJobs.Script.Diagnostics;
using Microsoft.Azure.WebJobs.Script.Eventing;
using Microsoft.Azure.WebJobs.Script.Grpc.Eventing;
using Microsoft.Azure.WebJobs.Script.Grpc.Messages;
using Microsoft.Extensions.Logging;

namespace Microsoft.Azure.WebJobs.Script.Tests.Workers.Rpc
{
    public class TestFunctionRpcService
    {
        private IScriptEventManager _eventManager;
        private ILogger _logger;
        private string _workerId;
        private IDictionary<string, IDisposable> _outboundEventSubscriptions = new Dictionary<string, IDisposable>();
        private ChannelWriter<InboundGrpcEvent> _inboundWriter;
        private ConcurrentDictionary<StreamingMessage.ContentOneofCase, Action> _handlers = new ConcurrentDictionary<StreamingMessage.ContentOneofCase, Action>();

        public TestFunctionRpcService(IScriptEventManager eventManager, string workerId, TestLogger logger, string expectedLogMsg = "")
        {
            _eventManager = eventManager;
            _logger = logger;
            _workerId = workerId;
            if (eventManager.TryGetGrpcChannels(workerId, out var inbound, out var outbound))
            {
                _ = ListenAsync(outbound.Reader, expectedLogMsg);
                _inboundWriter = inbound.Writer;

                PublishStartStreamEvent(); // simulate the start-stream immediately
            }
        }

        public void OnMessage(StreamingMessage.ContentOneofCase messageType, Action callback)
            => _handlers.AddOrUpdate(messageType, callback, (messageType, oldValue) => oldValue + callback);

        public void AutoReply(StreamingMessage.ContentOneofCase messageType)
        {
            // apply standard default responses
            Action callback = messageType switch
            {
                StreamingMessage.ContentOneofCase.FunctionEnvironmentReloadRequest => PublishFunctionEnvironmentReloadResponseEvent,
                _ => null,
            };
            if (callback is not null)
            {
                OnMessage(messageType, callback);
            }
        }

        private void OnMessage(StreamingMessage.ContentOneofCase messageType)
        {
            if (_handlers.TryRemove(messageType, out var action))
            {
                try
                {
                    _logger.LogDebug("[service] invoking auto-reply for {0}, {1}: {2}", _workerId, messageType, action?.Method?.Name);
                    action?.Invoke();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex.Message);
                }
            }
        }

        private async Task ListenAsync(ChannelReader<OutboundGrpcEvent> source, string expectedLogMsg)
        {
            await Task.Yield(); // free up caller
            try
            {
                while (await source.WaitToReadAsync())
                {
                    while (source.TryRead(out var evt))
                    {
                        _logger.LogDebug("[service] received {0}, {1}", evt.WorkerId, evt.MessageType);
                        _logger.LogInformation(expectedLogMsg);

                        OnMessage(evt.MessageType);
                    }
                }
            }
            catch
            {
            }
        }

        private ValueTask WriteAsync(StreamingMessage message)
            => _inboundWriter is null ? default
            : _inboundWriter.WriteAsync(new InboundGrpcEvent(_workerId, message));

        private void Write(StreamingMessage message)
        {
            if (_inboundWriter is null)
            {
                _logger.LogDebug("[service] no writer for {0}, {1}", _workerId, message.ContentCase);
                return;
            }
            var evt = new InboundGrpcEvent(_workerId, message);
            _logger.LogDebug("[service] sending {0}, {1}", evt.WorkerId, evt.MessageType);
            if (_inboundWriter.TryWrite(evt))
            {
                return;
            }
            var vt = _inboundWriter.WriteAsync(evt);
            if (vt.IsCompleted)
            {
                try
                {
                    vt.GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex.Message);
                }
            }
            else
            {
                _ = ObserveEventually(vt, _logger);
            }
            static async Task ObserveEventually(ValueTask valueTask, ILogger logger)
            {
                try
                {
                    await valueTask;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex.Message);
                }
            }
        }

        public void PublishFunctionLoadResponseEvent(string functionId)
        {
            StatusResult statusResult = new StatusResult()
            {
                Status = StatusResult.Types.Status.Success
            };
            FunctionLoadResponse functionLoadResponse = new FunctionLoadResponse()
            {
                FunctionId = functionId,
                Result = statusResult
            };
            StreamingMessage responseMessage = new StreamingMessage()
            {
                FunctionLoadResponse = functionLoadResponse
            };
            Write(responseMessage);
        }

        public void PublishFunctionLoadResponsesEvent(List<string> functionIds, StatusResult statusResult)
        {
            FunctionLoadResponseCollection functionLoadResponseCollection = new FunctionLoadResponseCollection();

            foreach (string functionId in functionIds)
            {
                FunctionLoadResponse functionLoadResponse = new FunctionLoadResponse()
                {
                    FunctionId = functionId,
                    Result = statusResult
                };

                functionLoadResponseCollection.FunctionLoadResponses.Add(functionLoadResponse);
            }

            StreamingMessage responseMessage = new StreamingMessage()
            {
                FunctionLoadResponseCollection = functionLoadResponseCollection
            };
            Write(responseMessage);
        }

        private void PublishFunctionEnvironmentReloadResponseEvent()
        {
            FunctionEnvironmentReloadResponse relaodEnvResponse = GetTestFunctionEnvReloadResponse();
            StreamingMessage responseMessage = new StreamingMessage()
            {
                FunctionEnvironmentReloadResponse = relaodEnvResponse
            };
            Write(responseMessage);
        }

        public void PublishWorkerInitResponseEvent(IDictionary<string, string> capabilities = null)
        {
            StatusResult statusResult = new StatusResult()
            {
                Status = StatusResult.Types.Status.Success
            };

            WorkerInitResponse initResponse = new WorkerInitResponse()
            {
                Result = statusResult
            };

            if (capabilities != null)
            {
                initResponse.Capabilities.Add(capabilities);
            }

            StreamingMessage responseMessage = new StreamingMessage()
            {
                WorkerInitResponse = initResponse
            };

            Write(responseMessage);
        }

        private void PublishWorkerInitResponseEventWithSharedMemoryDataTransferCapability()
        {
            StatusResult statusResult = new StatusResult()
            {
                Status = StatusResult.Types.Status.Success
            };
            WorkerInitResponse initResponse = new WorkerInitResponse()
            {
                Result = statusResult
            };
            StreamingMessage responseMessage = new StreamingMessage()
            {
                WorkerInitResponse = initResponse
            };
            Write(responseMessage);
        }

        public void PublishSystemLogEvent(RpcLog.Types.Level inputLevel)
        {
            RpcLog rpcLog = new RpcLog()
            {
                LogCategory = RpcLog.Types.RpcLogCategory.System,
                Level = inputLevel,
                Message = "Random system log message",
            };

            StreamingMessage logMessage = new StreamingMessage()
            {
                RpcLog = rpcLog
            };
            Write(logMessage);
        }

        public static FunctionEnvironmentReloadResponse GetTestFunctionEnvReloadResponse()
        {
            StatusResult statusResult = new StatusResult()
            {
                Status = StatusResult.Types.Status.Success
            };
            FunctionEnvironmentReloadResponse relaodEnvResponse = new FunctionEnvironmentReloadResponse()
            {
                Result = statusResult
            };
            return relaodEnvResponse;
        }

        public void PublishInvocationResponseEvent()
        {
            StatusResult statusResult = new StatusResult()
            {
                Status = StatusResult.Types.Status.Success
            };
            InvocationResponse invocationResponse = new InvocationResponse()
            {
                InvocationId = "TestInvocationId",
                Result = statusResult
            };
            StreamingMessage responseMessage = new StreamingMessage()
            {
                InvocationResponse = invocationResponse
            };
            Write(responseMessage);
        }

        private void PublishStartStreamEvent()
        {
            StatusResult statusResult = new StatusResult()
            {
                Status = StatusResult.Types.Status.Success
            };
            StartStream startStream = new StartStream()
            {
                WorkerId = _workerId
            };
            StreamingMessage responseMessage = new StreamingMessage()
            {
                StartStream = startStream
            };
            Write(responseMessage);
        }

        public void PublishWorkerMetadataResponse(string functionId, IEnumerable<FunctionMetadata> functionMetadata, bool successful, bool useDefaultMetadataIndexing = false)
        {
            StatusResult statusResult = new StatusResult();
            if (successful)
            {
                statusResult.Status = StatusResult.Types.Status.Success;
            }
            else
            {
                statusResult.Status = StatusResult.Types.Status.Failure;
            }

            FunctionMetadataResponse overallResponse = new FunctionMetadataResponse();
            overallResponse.UseDefaultMetadataIndexing = useDefaultMetadataIndexing;

            foreach (FunctionMetadata response in functionMetadata)
            {
                RpcFunctionMetadata indexingResponse = new RpcFunctionMetadata()
                {
                    Name = response.Name,
                    Language = response.Language,
                    Status = statusResult,
                    FunctionId = functionId
                };

                overallResponse.FunctionMetadataResults.Add(indexingResponse);
            }

            StreamingMessage responseMessage = new StreamingMessage()
            {
                FunctionMetadataResponse = overallResponse
            };
            Write(responseMessage);
        }
    }
}