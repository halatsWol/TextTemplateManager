using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TextTemplateManager.Helpers
{
    /// <summary>
    /// Per-user single-instance guard with a tiny named-pipe channel. A second launch (e.g. Windows
    /// opening a .ttmdata file, which starts another ttm.exe with the path as an argument) hands its
    /// message to the already-running instance and exits, so the app never runs twice.
    /// </summary>
    public sealed class SingleInstance : IDisposable
    {
        // Local\ scopes the mutex to the sign-in session; the pipe name is machine-global, so it
        // carries the user name to keep two different users' instances from crossing wires.
        private const string MutexName = @"Local\TextTemplateManager.SingleInstance";
        private static readonly string PipeName = "TextTemplateManager.Ipc." + SanitizedUser();

        private Mutex? _mutex;
        private CancellationTokenSource? _cts;

        /// <summary>Raised on the first instance when another launch sends a message. Fires on a
        /// background thread — marshal to the UI thread before touching any UI.</summary>
        public event Action<string>? MessageReceived;

        /// <summary>True when this process is the first (owning) instance.</summary>
        public bool TryAcquire()
        {
            _mutex = new Mutex(true, MutexName, out bool createdNew);
            return createdNew;
        }

        /// <summary>First instance only: start listening for messages from later launches.</summary>
        public void StartServer()
        {
            _cts = new CancellationTokenSource();
            _ = ServerLoopAsync(_cts.Token);
        }

        private async Task ServerLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(ct);
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    string msg = await reader.ReadToEndAsync(ct);
                    if (!string.IsNullOrEmpty(msg)) MessageReceived?.Invoke(msg);
                }
                catch (OperationCanceledException) { break; }
                catch { /* drop a bad/partial connection and keep listening */ }
            }
        }

        /// <summary>Second instance: deliver a message to the running instance (best effort).</summary>
        public static void SendToRunningInstance(string message)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                client.Connect(2000);
                using var writer = new StreamWriter(client, new UTF8Encoding(false));
                writer.Write(message);
                writer.Flush();
            }
            catch { /* the running instance may be shutting down — nothing we can do */ }
        }

        private static string SanitizedUser()
        {
            var sb = new StringBuilder();
            foreach (char c in Environment.UserName)
                sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            return sb.Length > 0 ? sb.ToString() : "user";
        }

        public void Dispose()
        {
            try { _cts?.Cancel(); } catch { }
            try { _mutex?.Dispose(); } catch { }
        }
    }
}
