// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.IO.Pipes;
using WinApp.HotReload.Protocol;

namespace WinApp.HotReload;

/// <summary>Request/response channel to the agent running inside the app.</summary>
internal sealed class AgentClient(NamedPipeClientStream pipe, string token) : IDisposable
{
    public static async Task<AgentClient> ConnectAsync(string pipeName, string token, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (true)
        {
            var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.ConnectAsync(2000, cancellationToken).ConfigureAwait(false);
                return new AgentClient(pipe, token);
            }
            catch (TimeoutException) when (DateTime.UtcNow < deadline)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    public async Task<HotReloadResponse> SendAsync(HotReloadRequest request, CancellationToken cancellationToken)
    {
        request.Token = token;
        await HotReloadFrames.WriteAsync(pipe, request, cancellationToken).ConfigureAwait(false);
        return await HotReloadFrames.ReadAsync<HotReloadResponse>(pipe, cancellationToken).ConfigureAwait(false)
            ?? throw new EndOfStreamException("The app closed the hot reload connection.");
    }

    public void Dispose() => pipe.Dispose();
}
