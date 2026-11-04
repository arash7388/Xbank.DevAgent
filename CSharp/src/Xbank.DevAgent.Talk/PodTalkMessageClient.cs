using Microsoft.Extensions.Options;
using POD_Async.Base;
using POD_Async.Model;
using POD_Chat;
using POD_Chat.Base;
using POD_Chat.Base.Enum;
using POD_Chat.Model.ServiceOutput;
using POD_Chat.Model.ValueObject;

namespace Xbank.DevAgent.Talk;

// Registered once per host: the underlying SDK owns a process-wide ServiceLocator.
public sealed class PodTalkMessageClient(IOptions<TalkOptions> options) : ITalkMessageClient
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _initialized;
    private bool _authenticated;

    public async Task SendAsync(long threadId, string text, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            TalkOptions settings = options.Value;
            if (!settings.Enabled) return;
            if (!settings.IsValid()) throw new InvalidOperationException("Talk configuration is incomplete.");
            cancellationToken.ThrowIfCancellationRequested();
            if (!_initialized)
            {
                AsyncConfig config = AsyncConfig.ConcreteBuilder
                    .SetQueueUrl([QueueUrl.ConcreteBuilder.SetIp(settings.QueueHost).SetPort(settings.QueuePort).Build()])
                    .SetQueueUsername(settings.QueueUsername).SetQueuePassword(settings.QueuePassword)
                    .SetQueueReceive(settings.QueueReceive).SetQueueSend(settings.QueueSend)
                    .SetQueueConnectionTimeout(settings.TimeoutSeconds * 1000L)
                    .SetServerName(settings.ServerName).SetFileServer(settings.FileServer)
                    .SetPodspaceHost(settings.PodspaceHost).SetPlatformHost(settings.PlatformHost)
                    .SetSsoHost(settings.SsoHost).SetConsumersCount(1).Build();
                _ = new ServiceLocator(new ChatConfig(config, settings.Token));
                _initialized = true;
            }

            if (!_authenticated)
            {
                TaskCompletionSource authenticated = new(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnUserInfo(ChatResponseSrv<GetUserInfoResponse> response)
                {
                    if (!response.HasError) authenticated.TrySetResult();
                }
                ServiceLocator.ResponseHandler.UserInfo_MessageReceived += OnUserInfo;
                try
                {
                    ServiceLocator.ChatService.GetUserInfo();
                    await authenticated.Task.WaitAsync(TimeSpan.FromSeconds(settings.TimeoutSeconds), cancellationToken);
                    _authenticated = true;
                }
                finally { ServiceLocator.ResponseHandler.UserInfo_MessageReceived -= OnUserInfo; }
            }

            string uniqueId = Guid.NewGuid().ToString("D");
            TaskCompletionSource sent = new(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnSent(ChatResponseSrv<SendMessageModel> response)
            {
                if (response.UniqueId != uniqueId) return;
                if (response.HasError) sent.TrySetException(new InvalidOperationException("Talk rejected the message."));
                else sent.TrySetResult();
            }
            void OnChatError(ChatResponseSrv<AsyncErrorMessage> response)
            {
                if (response.UniqueId == uniqueId || response.Result?.UniqueId == uniqueId)
                    sent.TrySetException(new InvalidOperationException("Talk rejected the message."));
            }
            void OnAsyncError(AsyncErrorMessage response)
            {
                if (response.UniqueId == uniqueId)
                    sent.TrySetException(new InvalidOperationException("Talk transport failed."));
            }

            ResponseHandler handler = ServiceLocator.ResponseHandler;
            handler.Sent_MessageReceived += OnSent;
            handler.ChatError_MessageReceived += OnChatError;
            handler.AsyncError += OnAsyncError;
            try
            {
                ServiceLocator.ChatService.SendTextMessage(SendTextMessageRequest.ConcreteBuilder
                    .SetThreadId(threadId).SetTextMessage(text).SetMessageType(MessageType.TEXT).SetUniqueId(uniqueId).Build());
                await sent.Task.WaitAsync(TimeSpan.FromSeconds(settings.TimeoutSeconds), cancellationToken);
            }
            finally
            {
                handler.Sent_MessageReceived -= OnSent;
                handler.ChatError_MessageReceived -= OnChatError;
                handler.AsyncError -= OnAsyncError;
            }
        }
        finally { _gate.Release(); }
    }
}
