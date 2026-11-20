using System;
using System.Threading;
using Concentus;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using PocketXR.Core;

namespace PocketXR.PcScreens;

/// <summary>
/// PC screens: the other computer's sound, Opus from the stream decoded (Concentus) and played on this PC's own output
/// (WASAPI shared, low latency). Only one PC screen plays sound, as on the phone: PC screen 1 unless its sound is off.
/// PXR_PCSCREEN_MUTE=1 (tests): decoded and counted, not played (a loopback test would otherwise echo).
/// </summary>
internal sealed class PcScreenSound : IDisposable
{
    private readonly IOpusDecoder decoder;
    private readonly int channels, frameSamples;
    private readonly BufferedWaveProvider? buffer;
    private readonly WasapiOut? output;
    private readonly short[] pcm;
    private readonly byte[] bytes;
    public long Decoded, Errors;

    public PcScreenSound(int sampleRate, int channels, int samplesPerFrame, bool play)
    {
        this.channels = channels;
        frameSamples = samplesPerFrame;
        decoder = OpusCodecFactory.CreateDecoder(sampleRate, channels);
        pcm = new short[samplesPerFrame * channels * 6];
        bytes = new byte[pcm.Length * 2];
        if (!play) return;
        try
        {
            buffer = new BufferedWaveProvider(new WaveFormat(sampleRate, 16, channels))
            {
                BufferDuration = TimeSpan.FromMilliseconds(500),
                DiscardOnBufferOverflow = true,
            };
            output = new WasapiOut(AudioClientShareMode.Shared, true, 30);
            output.Init(buffer);
            output.Play();
        }
        catch (Exception e)
        {
            Log.P($"pc screen: no sound out: {e.Message}");
            output?.Dispose();
            output = null;
            buffer = null;
        }
    }

    public void Sample(ReadOnlySpan<byte> opus)
    {
        int n;
        try { n = decoder.Decode(opus, pcm, pcm.Length / channels, false); }
        catch (Exception) { Errors++; return; }
        if (n <= 0) { Errors++; return; }
        Decoded++;
        if (buffer == null) return;
        // Little behind keeps latency low: more than 100 ms queued, and the oldest goes.
        if (buffer.BufferedDuration.TotalMilliseconds > 100) buffer.ClearBuffer();
        Buffer.BlockCopy(pcm, 0, bytes, 0, n * channels * 2);
        buffer.AddSamples(bytes, 0, n * channels * 2);
    }

    public void Dispose()
    {
        try { output?.Stop(); } catch (Exception) { }
        output?.Dispose();
    }
}
