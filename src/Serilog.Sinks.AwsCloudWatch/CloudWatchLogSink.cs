using Amazon.CloudWatchLogs;
using Amazon.CloudWatchLogs.Model;
using Serilog.Events;
using Serilog.Sinks.PeriodicBatching;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Serilog.Sinks.AwsCloudWatch
{
    public class CloudWatchLogSink : PeriodicBatchingSink
    {
        /// <summary>
        /// The maximum log event size = 256 KB - 26 bytes
        /// </summary>
        public const int MaxLogEventSize = 262118;

        /// <summary>
        /// The maximum log event batch size = 1 MB
        /// </summary>
        public const int MaxLogEventBatchSize = 1048576;

        /// <summary>
        /// The maximum log event batch count
        /// </summary>
        public const int MaxLogEventBatchCount = 10000;

        /// <summary>
        /// The maximum span of events in a batch
        /// </summary>
        public static readonly TimeSpan MaxBatchEventSpan = TimeSpan.FromDays(1);

        /// <summary>
        /// The span of time to throttle requests at
        /// </summary>
        public static readonly TimeSpan ThrottlingInterval = TimeSpan.FromMilliseconds(200);

        /// <summary>
        /// The span of time to backoff when an error occurs
        /// </summary>
        public static readonly TimeSpan ErrorBackoffStartingInterval = TimeSpan.FromMilliseconds(100);

        private readonly IAmazonCloudWatchLogs cloudWatchClient;
        private readonly CloudWatchSinkOptions options;
        private bool hasInit;
        private string logStreamName;
        private string nextSequenceToken;
        private readonly ILogEventRenderer renderer;

        public CloudWatchLogSink(IAmazonCloudWatchLogs cloudWatchClient, CloudWatchSinkOptions options)
            : base(options.BatchSizeLimit, options.Period)
        {
            if (options.BatchSizeLimit < 1)
            {
                throw new ArgumentException($"{nameof(CloudWatchSinkOptions)}.{nameof(options.BatchSizeLimit)} must be a value greater than 0.");
            }
            this.cloudWatchClient = cloudWatchClient;
            this.options = options;
            this.renderer = options.LogEventRenderer ?? new RenderedMessageLogEventRenderer();
        }

        private async Task EnsureInitializedAsync()
        {
            if (hasInit)
            {
                return;
            }

            // create log group
            await CreateLogGroupAsync();

            // create log stream
            UpdateLogStreamName();
            await CreateLogStreamAsync();

            hasInit = true;
        }

        private void UpdateLogStreamName()
        {
            logStreamName = options.LogStreamNameProvider.GetLogStreamName();
            nextSequenceToken = null; // always reset on a new stream
        }

        private async Task CreateLogGroupAsync()
        {
            if (options.CreateLogGroup)
            {
                // see if the log group already exists
                DescribeLogGroupsRequest describeRequest = new DescribeLogGroupsRequest { LogGroupNamePrefix = options.LogGroupName };
                var logGroups = await cloudWatchClient.DescribeLogGroupsAsync(describeRequest);
                var logGroup = logGroups.LogGroups.FirstOrDefault(lg => string.Equals(lg.LogGroupName, options.LogGroupName, StringComparison.OrdinalIgnoreCase));

                // create log group if it doesn't exist
                if (logGroup == null)
                {
                    CreateLogGroupRequest createRequest = new CreateLogGroupRequest(options.LogGroupName);
                    var createResponse = await cloudWatchClient.CreateLogGroupAsync(createRequest);
                    if (!createResponse.HttpStatusCode.IsSuccessStatusCode())
                    {
                        throw new AwsCloudWatchSinkException($"Tried to create a log group, but failed with status code '{createResponse.HttpStatusCode}' and data '{createResponse.ResponseMetadata.FlattenedMetaData()}'.");
                    }
                }
            }
        }

        private async Task CreateLogStreamAsync()
        {
            CreateLogStreamRequest createLogStreamRequest = new CreateLogStreamRequest()
            {
                LogGroupName = options.LogGroupName,
                LogStreamName = logStreamName
            };
            var createLogStreamResponse = await cloudWatchClient.CreateLogStreamAsync(createLogStreamRequest);
            if (!createLogStreamResponse.HttpStatusCode.IsSuccessStatusCode())
            {
                throw new AwsCloudWatchSinkException(
                    $"Tried to create a log stream, but failed with status code '{createLogStreamResponse.HttpStatusCode}' and data '{createLogStreamResponse.ResponseMetadata.FlattenedMetaData()}'.");
            }
        }

        private async Task UpdateLogStreamSequenceTokenAsync()
        {
            DescribeLogStreamsRequest describeLogStreamsRequest = new DescribeLogStreamsRequest
            {
                LogGroupName = options.LogGroupName,
                LogStreamNamePrefix = logStreamName
            };
            var describeLogStreamsResponse = await cloudWatchClient.DescribeLogStreamsAsync(describeLogStreamsRequest);
            if (!describeLogStreamsResponse.HttpStatusCode.IsSuccessStatusCode())
            {
                throw new AwsCloudWatchSinkException(
                    $"Failed to describe log streams with status code '{describeLogStreamsResponse.HttpStatusCode}' and data '{describeLogStreamsResponse.ResponseMetadata.FlattenedMetaData()}'.");
            }
            nextSequenceToken = describeLogStreamsResponse.NextToken;
        }

        protected override async Task EmitBatchAsync(IEnumerable<LogEvent> events)
        {
            if (events?.Count() == 0)
            {
                return;
            }

            // We do not need synchronization in this method since it is only called from a single thread by the PeriodicBatchSink.

            try
            {
                await EnsureInitializedAsync();
            }
            catch (Exception ex)
            {
                Debugging.SelfLog.WriteLine("Error initializing log stream. No logs will be sent to AWS CloudWatch. Exception was {0}.", ex);
                return;
            }

            try
            {
                var logEvents =
                    new Queue<InputLogEvent>(events
                        .OrderBy(e => e.Timestamp) // log events need to be ordered by timestamp within a single bulk upload to CloudWatch
                        .Select( // transform
                            @event =>
                            {
                                var message = renderer.RenderLogEvent(@event);
                                if (message.Length > MaxLogEventSize)
                                {
                                    // truncate event message
                                    Debugging.SelfLog.WriteLine("Truncating log event with length of {0}", message.Length);
                                    message = message.Substring(0, MaxLogEventSize);
                                }
                                return new InputLogEvent
                                {
                                    Message = message,
                                    Timestamp = @event.Timestamp.UtcDateTime
                                };
                            }));

                while (logEvents.Count > 0)
                {
                    DateTime? first = null;
                    var batchSize = 0;
                    var batch = new List<InputLogEvent>();

                    // build batch
                    do
                    {
                        var @event = logEvents.Peek();
                        if (!first.HasValue)
                        {
                            first = @event.Timestamp;
                        }
                        else if (@event.Timestamp.Subtract(first.Value) > MaxBatchEventSpan) // ensure batch spans no more than 24 hours
                        {
                            break;
                        }

                        if (batchSize + @event.Message.Length < MaxLogEventBatchSize) // ensure < max batch size
                        {
                            batchSize += @event.Message.Length;
                            batch.Add(@event);
                            logEvents.Dequeue();
                        }
                        else
                        {
                            break;
                        }
                    } while (batch.Count < MaxLogEventBatchCount && logEvents.Count > 0); // ensure < max batch count

                    // creates the request to upload a new event to CloudWatch
                    PutLogEventsRequest putEventsRequest = new PutLogEventsRequest
                    {
                        LogGroupName = options.LogGroupName,
                        LogStreamName = logStreamName,
                        SequenceToken = nextSequenceToken,
                        LogEvents = batch
                    };

                    var success = false;
                    var attempt = 0;
                    while (!success && attempt <= options.RetryAttempts)
                    {
                        try
                        {
                            // actually upload the event to CloudWatch
                            var putLogEventsResponse = await cloudWatchClient.PutLogEventsAsync(putEventsRequest);

                            if (putLogEventsResponse.HttpStatusCode.IsSuccessStatusCode())
                            {
                                // remember the next sequence token, which is required
                                nextSequenceToken = putLogEventsResponse.NextSequenceToken;

                                // throttle
                                await Task.Delay(ThrottlingInterval);

                                success = true;
                            }
                        }
                        catch (ServiceUnavailableException e)
                        {
                            // retry with back-off
                            Debugging.SelfLog.WriteLine("Service unavailable.  Attempt: {0}  Error: {1}", attempt, e);
                            await Task.Delay(ErrorBackoffStartingInterval.Milliseconds * (int)Math.Pow(2, attempt));
                            attempt++;
                        }
                        catch (InvalidParameterException e)
                        {
                            // cannot modify request without investigation
                            Debugging.SelfLog.WriteLine("Invalid parameter.  Error: {0}", e);
                            break;
                        }
                        catch (ResourceNotFoundException e)
                        {
                            // no retry with back-off because..
                            //   if one of these fails, we get out of the loop.
                            //   if they're both successful, we don't hit this case again.
                            Debugging.SelfLog.WriteLine("Resource was not found.  Error: {0}", e);
                            await CreateLogGroupAsync();
                            await CreateLogStreamAsync();
                        }
                        catch (DataAlreadyAcceptedException e)
                        {
                            Debugging.SelfLog.WriteLine("Data already accepted.  Attempt: {0}  Error: {1}", attempt, e);
                            try
                            {
                                await UpdateLogStreamSequenceTokenAsync();
                            }
                            catch (Exception ex)
                            {
                                Debugging.SelfLog.WriteLine("Unable to update log stream sequence.  Attempt: {0}  Error: {1}", attempt, ex);

                                // try again with a different log stream
                                UpdateLogStreamName();
                                await CreateLogStreamAsync();
                                putEventsRequest.LogStreamName = logStreamName;
                            }
                            attempt++; // don't think this is case we care about incrementing
                        }
                        catch (InvalidSequenceTokenException e)
                        {
                            Debugging.SelfLog.WriteLine("Invalid sequence token.  Attempt: {0}  Error: {1}", attempt, e);
                            try
                            {
                                await UpdateLogStreamSequenceTokenAsync();
                            }
                            catch (Exception ex)
                            {
                                Debugging.SelfLog.WriteLine("Unable to update log stream sequence.  Attempt: {0}  Error: {1}", attempt, ex);

                                // try again with a different log stream
                                UpdateLogStreamName();
                                await CreateLogStreamAsync();
                                putEventsRequest.LogStreamName = logStreamName;
                            }
                            attempt++; // don't think this is case we care about incrementing
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                try
                {
                    Debugging.SelfLog.WriteLine("Error sending logs. No logs will be sent to AWS CloudWatch. Error was {0}", ex);
                }
                catch (Exception)
                {
                    // we even failed to log to the trace logger - giving up trying to put something out
                }
            }
        }
    }
}
