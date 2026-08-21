#nullable enable
  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023-2024 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// OmenMon-Reborn additions © 2026 seakyy

using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OmenMon.Library {

    // Implements a named-pipe server for publishing live fan RPM telemetry
    // to third-party tools (e.g. DeltaT, HWiNFO) without touching the EC (issue #131).
    // Avoids EC conflicts when multiple PawnIO applications run concurrently.
    public sealed class FanDataPipeServer : IDisposable {

        public const string PipeName = "OmenMon_FanData";

        private static readonly FanDataPipeServer _instance = new FanDataPipeServer();
        public static FanDataPipeServer Instance => _instance;

        public static bool Enabled { get; set; } = true;

        private readonly object _lock = new object();
        private readonly AutoResetEvent _serverReady = new AutoResetEvent(false);

        private CancellationTokenSource? _cts;
        private Task? _listenTask;
        private volatile bool _running;
        private NamedPipeServerStream? _activePipe;

        private int _latestCpuRpm;
        private int _latestGpuRpm;
        private bool _hasData;

        public bool IsRunning => _running;

        private FanDataPipeServer() { }

        public static string FormatJson(int cpuRpm, int gpuRpm) {
            return string.Format("{{\"cpu\":{0},\"gpu\":{1}}}\n",
                cpuRpm > 0 ? cpuRpm : 0,
                gpuRpm > 0 ? gpuRpm : 0);
        }

        public void Start() {
            lock (_lock) {
                if (_running || !Enabled) return;
                _running = true;
                _cts = new CancellationTokenSource();
                _serverReady.Reset();
                _listenTask = Task.Run(() => ListenLoopAsync(_cts.Token));
            }
            _serverReady.WaitOne(2000);
        }

        public void Stop() {
            Task? taskToWait = null;
            lock (_lock) {
                if (!_running) return;
                _running = false;
                try {
                    _cts?.Cancel();
                } catch { }
                DisconnectActivePipe();
                taskToWait = _listenTask;
                _listenTask = null;
            }
            if (taskToWait != null) {
                try {
                    taskToWait.Wait(500);
                } catch { }
            }
        }

        public void Publish(int cpuRpm, int gpuRpm) {
            if (!Enabled) return;

            if (!_running) {
                Start();
            }

            _latestCpuRpm = cpuRpm;
            _latestGpuRpm = gpuRpm;
            _hasData = true;

            lock (_lock) {
                if (_activePipe != null && _activePipe.IsConnected) {
                    try {
                        byte[] bytes = Encoding.UTF8.GetBytes(FormatJson(cpuRpm, gpuRpm));
                        _activePipe.Write(bytes, 0, bytes.Length);
                        _activePipe.Flush();
                    } catch {
                        DisconnectActivePipe();
                    }
                }
            }
        }

        private async Task ListenLoopAsync(CancellationToken token) {
            while (_running && !token.IsCancellationRequested) {
                NamedPipeServerStream? pipe = null;
                try {
                    pipe = new NamedPipeServerStream(
                        PipeName,
                        PipeDirection.Out,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.None);

                    lock (_lock) {
                        _activePipe = pipe;
                    }

                    _serverReady.Set();

                    await Task.Run(() => {
                        try {
                            pipe.WaitForConnection();
                        } catch { }
                    }, token).ConfigureAwait(false);

                    if (_hasData && pipe.IsConnected) {
                        lock (_lock) {
                            try {
                                byte[] bytes = Encoding.UTF8.GetBytes(FormatJson(_latestCpuRpm, _latestGpuRpm));
                                pipe.Write(bytes, 0, bytes.Length);
                                pipe.Flush();
                            } catch { }
                        }
                    }

                    while (_running && !token.IsCancellationRequested && _activePipe == pipe && pipe.IsConnected) {
                        await Task.Delay(50, token).ConfigureAwait(false);
                    }
                } catch (OperationCanceledException) {
                    break;
                } catch (ObjectDisposedException) {
                    break;
                } catch (Exception) {
                    _serverReady.Set();
                    if (!_running || token.IsCancellationRequested) break;
                    try {
                        await Task.Delay(100, token).ConfigureAwait(false);
                    } catch {
                        break;
                    }
                } finally {
                    DisconnectActivePipe();
                }
            }
        }

        private void DisconnectActivePipe() {
            lock (_lock) {
                if (_activePipe != null) {
                    try {
                        if (_activePipe.IsConnected) {
                            _activePipe.Disconnect();
                        }
                    } catch { }
                    try {
                        _activePipe.Dispose();
                    } catch { }
                    _activePipe = null;
                }
            }
        }

        public void Dispose() {
            Stop();
            _serverReady.Dispose();
            _cts?.Dispose();
        }
    }
}
