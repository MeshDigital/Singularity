namespace Singularity.Karaoke.Audio;

/// <summary>Which part of a capture device a singer uses.</summary>
public enum MicChannel
{
    /// <summary>All channels mixed: one microphone on the device.</summary>
    Mix,
    /// <summary>Left channel: player 1 on a two-mic karaoke adapter (which shows up as one stereo device).</summary>
    Left,
    /// <summary>Right channel: player 2 on a two-mic adapter.</summary>
    Right,
}

/// <summary>One block from a capture device: interleaved samples in -1..1 and the time of its first frame.</summary>
public readonly record struct MicBlock(float[] Interleaved, int Frames, int Channels, double TimeMs)
{
    /// <summary>
    /// Writes one singer's samples into <paramref name="destination"/> (at least <see cref="Frames"/> long)
    /// and returns the frame count. On a mono device every channel choice reads the only channel.
    /// </summary>
    public int Extract(MicChannel channel, float[] destination)
    {
        int c = Channels;
        if (c == 1 || channel == MicChannel.Left || (channel == MicChannel.Right && c < 2))
        {
            for (int f = 0; f < Frames; f++) destination[f] = Interleaved[f * c];
        }
        else if (channel == MicChannel.Right)
        {
            for (int f = 0; f < Frames; f++) destination[f] = Interleaved[f * c + 1];
        }
        else
        {
            for (int f = 0; f < Frames; f++)
            {
                float sum = 0;
                for (int k = 0; k < c; k++) sum += Interleaved[f * c + k];
                destination[f] = sum / c;
            }
        }
        return Frames;
    }
}
