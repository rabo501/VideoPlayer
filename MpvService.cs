using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VideoPlayer
{
    /// <summary>Obsługuje proces mpv i jego interfejs IPC.</summary>
    internal sealed class MpvService : IAsyncDisposable
    {
        private readonly string _pipeName = "VideoPlayer_" + Guid.NewGuid().ToString("N");
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private Process? _process;
        private NamedPipeClientStream? _pipe;
        private StreamWriter? _writer;
        private bool _disposed;

        public bool IsReady { get; private set; }
        public event Action<JsonElement>? MessageReceived;

        public async Task StartAsync(string executable, IntPtr windowHandle)
        {
            if (!File.Exists(executable))
                throw new FileNotFoundException("Nie znaleziono mpv.exe w folderze mpv.", executable);

            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            string[] arguments =
            {
                "--no-config", "--idle=yes", "--keep-open=always",
                "--keep-open-pause=yes", "--terminal=no", "--osc=no",
                "--input-default-bindings=no", "--input-vo-keyboard=no",
                "--wid=" + unchecked((uint)windowHandle.ToInt64()).ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--screenshot-jpeg-quality=95",
                "--input-ipc-server=\\\\.\\pipe\\" + _pipeName
            };
            foreach (string argument in arguments)
                startInfo.ArgumentList.Add(argument);

            _process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Nie udało się uruchomić mpv.");

            _pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await _pipe.ConnectAsync(10000).ConfigureAwait(false);
            _writer = new StreamWriter(_pipe, new UTF8Encoding(false), 4096, leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\n"
            };
            IsReady = true;
            _ = ReadMessagesAsync(_pipe);
        }

        public async Task SendAsync(params object[] command)
        {
            await SendMessageAsync(new { command }).ConfigureAwait(false);
        }

        public async Task SendRequestAsync(int requestId, params object[] command)
        {
            await SendMessageAsync(new { command, request_id = requestId }).ConfigureAwait(false);
        }

        private async Task SendMessageAsync(object payload)
        {
            if (!IsReady || _disposed) return;
            await _sendLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_writer == null || !IsReady || _disposed) return;
                await _writer.WriteLineAsync(JsonSerializer.Serialize(payload)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException)
            {
                IsReady = false;
                throw;
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private async Task ReadMessagesAsync(NamedPipeClientStream pipe)
        {
            try
            {
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
                while (!_disposed)
                {
                    string? line = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (line == null) break;
                    try
                    {
                        using JsonDocument document = JsonDocument.Parse(line);
                        MessageReceived?.Invoke(document.RootElement.Clone());
                    }
                    catch (JsonException) { }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException) { }
            finally { IsReady = false; }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            IsReady = false;
            try { _pipe?.Dispose(); } catch { }
            try
            {
                if (_process != null && !_process.HasExited)
                    _process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            _process?.Dispose();
            _writer?.Dispose();
            _sendLock.Dispose();
            await Task.CompletedTask;
        }
    }
}
