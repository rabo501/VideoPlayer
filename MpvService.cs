using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
namespace VideoPlayer
{
    internal sealed class MpvService : IAsyncDisposable
    {
        private readonly string _pipeName = "VideoPlayer_" + Guid.NewGuid().ToString("N");
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
        private readonly CancellationTokenSource _lifetime = new();
        private Process? _process;
        private NamedPipeClientStream? _pipe;
        private StreamWriter? _writer;
        private Task? _readerTask;
        private int _nextId;
        private bool _disposed;
        private int _disconnected;
        public bool IsReady { get; private set; }
        public event Action<JsonElement>? MessageReceived;
        public event Action<string>? Disconnected;

        public async Task StartAsync(string executable, IntPtr handle)
        {
            if (!File.Exists(executable)) throw new FileNotFoundException("Brak mpv.exe.", executable);
            string script = Path.Combine(AppContext.BaseDirectory,"scripts","review.lua");
            if (!File.Exists(script)) throw new FileNotFoundException("Brak scripts/review.lua.",script);
            var psi = new ProcessStartInfo(executable) { UseShellExecute=false, CreateNoWindow=true };
            foreach (string arg in new[] { "--no-config", "--idle=yes", "--keep-open=always",
                "--keep-open-pause=yes", "--terminal=no", "--osc=no", "--input-default-bindings=no",
                "--input-vo-keyboard=yes", "--input-cursor=yes", "--screenshot-jpeg-quality=95",
                "--wid="+handle.ToInt64().ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--script="+script, @"--input-ipc-server=\\.\pipe\"+_pipeName }) psi.ArgumentList.Add(arg);
            try
            {
                _process=Process.Start(psi) ?? throw new IOException("Nie uruchomiono mpv.");
                _pipe=new NamedPipeClientStream(".",_pipeName,PipeDirection.InOut,PipeOptions.Asynchronous);
                await _pipe.ConnectAsync(10000,_lifetime.Token).ConfigureAwait(false);
                _writer=new StreamWriter(_pipe,new UTF8Encoding(false),4096,true) { AutoFlush=true,NewLine="\n" };
                IsReady=true;
                _readerTask=ReadAsync();
            }
            catch { await DisposeAsync(); throw; }
        }
        public async Task<JsonElement> RequestAsync(object[] command, CancellationToken ct=default)
        {
            if (!IsReady || _disposed) throw new IOException("Brak połączenia z mpv.");
            int id=Interlocked.Increment(ref _nextId);
            var tcs=new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id]=tcs;
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct,_lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            try
            {
                await _sendLock.WaitAsync(timeout.Token).ConfigureAwait(false);
                try
                {
                    if (!IsReady || _writer==null) throw new IOException("Połączenie mpv zostało zamknięte.");
                    string line=JsonSerializer.Serialize(new {command,request_id=id});
                    await _writer.WriteLineAsync(line.AsMemory(),timeout.Token).ConfigureAwait(false);
                }
                finally { _sendLock.Release(); }
                JsonElement response=await tcs.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
                string error=response.GetProperty("error").GetString() ?? "unknown";
                if (error!="success") throw new InvalidOperationException("mpv: "+error);
                return response.TryGetProperty("data",out var data)?data.Clone():default;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && !_lifetime.IsCancellationRequested)
            { throw new TimeoutException("mpv nie odpowiedział w ciągu 12 sekund."); }
            finally { _pending.TryRemove(id,out _); }
        }
        public async Task SendAsync(params object[] command) { await RequestAsync(command).ConfigureAwait(false); }
        public async Task SendRequestAsync(int requestId,params object[] command) { await SendAsync(command); }
        private async Task ReadAsync()
        {
            string reason="Połączenie z mpv zostało zamknięte.";
            try
            {
                using var reader=new StreamReader(_pipe!,Encoding.UTF8,false,4096,true);
                while (!_lifetime.IsCancellationRequested)
                {
                    string? line=await reader.ReadLineAsync(_lifetime.Token).ConfigureAwait(false);
                    if (line==null) break;
                    try
                    {
                        using var doc=JsonDocument.Parse(line);
                        var message=doc.RootElement.Clone();
                        if (message.TryGetProperty("request_id",out var rid) && rid.TryGetInt32(out int id)
                            && _pending.TryGetValue(id,out var waiter)) waiter.TrySetResult(message);
                        MessageReceived?.Invoke(message);
                    }
                    catch (JsonException) { }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { reason=ex.Message; }
            finally
            {
                IsReady=false;
                foreach (var waiter in _pending.Values) waiter.TrySetException(new IOException(reason));
                if (!_disposed && Interlocked.Exchange(ref _disconnected,1)==0) Disconnected?.Invoke(reason);
            }
        }
        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed=true; IsReady=false; _lifetime.Cancel();
            _pipe?.Dispose();
            if (_readerTask!=null) { try { await _readerTask.ConfigureAwait(false); } catch { } }
            foreach (var waiter in _pending.Values) waiter.TrySetCanceled();
            // Take the same lock as senders; do not dispose it while an in-flight sender releases it.
            await _sendLock.WaitAsync().ConfigureAwait(false);
            try { try { _writer?.Dispose(); } catch { } } finally { _sendLock.Release(); }
            try { if (_process!=null && !_process.HasExited) { _process.Kill(true); await _process.WaitForExitAsync().ConfigureAwait(false); } } catch { }
            _process?.Dispose();
        }
    }
}
