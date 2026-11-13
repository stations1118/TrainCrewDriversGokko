using System.Media;

namespace Gokko.Client;

/// <summary>同梱WAVのループ再生。受信範囲外・切断・終了時は即停止する。</summary>
public sealed class ProtectionRadioAudio : IDisposable
{
    private readonly Stream _stream;
    private readonly SoundPlayer _player;
    private bool _playing;

    public ProtectionRadioAudio()
    {
        _stream = typeof(ProtectionRadioAudio).Assembly.GetManifestResourceStream(
            "Gokko.Client.Audio.bougo_radio_alarm.wav")
            ?? throw new InvalidOperationException("防護無線の音声が見つかりません");
        _player = new SoundPlayer(_stream);
        _player.Load();
    }

    public void Update(bool receiving)
    {
        if (_playing == receiving) return;
        if (receiving) _player.PlayLooping();
        else _player.Stop();
        _playing = receiving;
    }

    public void Dispose()
    {
        _player.Stop();
        _player.Dispose();
        _stream.Dispose();
    }
}
