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
using OmenMon.Library;
using Xunit;

namespace OmenMon.Tests {

    public class FanDataPipeServerTests {

        [Fact]
        public void FormatJson_ReturnsExpectedJsonLine() {
            string json = FanDataPipeServer.FormatJson(4022, 3623);
            Assert.Equal("{\"cpu\":4022,\"gpu\":3623}\n", json);

            string zeroJson = FanDataPipeServer.FormatJson(-1, 0);
            Assert.Equal("{\"cpu\":0,\"gpu\":0}\n", zeroJson);
        }

        [Fact]
        public async Task EcCollisionAvoidance_PipeClientReceivesPublishedTelemetry() {
            bool origConfig = FanDataPipeServer.Enabled;
            try {
                FanDataPipeServer.Enabled = true;
                FanDataPipeServer.Instance.Publish(4500, 4200);

                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5))) {
                    using (var client = new NamedPipeClientStream(".", FanDataPipeServer.PipeName, PipeDirection.In)) {
                        await client.ConnectAsync(cts.Token);
                        Assert.True(client.IsConnected);

                        byte[] buffer = new byte[256];
                        int bytesRead = await client.ReadAsync(buffer, 0, buffer.Length, cts.Token);
                        string received = Encoding.UTF8.GetString(buffer, 0, bytesRead);

                        Assert.Contains("\"cpu\":4500", received);
                        Assert.Contains("\"gpu\":4200", received);
                    }
                }
            } finally {
                FanDataPipeServer.Instance.Stop();
                FanDataPipeServer.Enabled = origConfig;
            }
        }

        [Fact]
        public void NonBlockingPublish_WhenNoClientConnected() {
            bool origConfig = FanDataPipeServer.Enabled;
            try {
                FanDataPipeServer.Enabled = true;
                FanDataPipeServer.Instance.Start();

                // Publish when no client is listening should not throw or block
                var watch = System.Diagnostics.Stopwatch.StartNew();
                FanDataPipeServer.Instance.Publish(3000, 2800);
                watch.Stop();

                Assert.True(watch.ElapsedMilliseconds < 500);
            } finally {
                FanDataPipeServer.Instance.Stop();
                FanDataPipeServer.Enabled = origConfig;
            }
        }

        [Fact]
        public void ConfigToggle_DisablesPipeServer() {
            bool origConfig = FanDataPipeServer.Enabled;
            try {
                FanDataPipeServer.Enabled = false;
                FanDataPipeServer.Instance.Stop();
                FanDataPipeServer.Instance.Start();

                Assert.False(FanDataPipeServer.Instance.IsRunning);
            } finally {
                FanDataPipeServer.Instance.Stop();
                FanDataPipeServer.Enabled = origConfig;
            }
        }
    }
}
