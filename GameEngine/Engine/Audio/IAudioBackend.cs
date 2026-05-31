using AsteroidsEngine.Engine.Resources;

namespace AsteroidsEngine.Engine.Audio;

/// <summary>
/// Audio playback abstraction — the audio half of the Platform Abstraction Layer.
/// Backends: NAudio (WinForms), OpenAL / SDL_mixer (SDL). Sounds are referenced
/// by SoundId obtained from IResourceManager.
/// </summary>
public interface IAudioBackend
{
    void PlaySound(SoundId id, float volume = 1f, float pan = 0f);
    void PlayMusic(SoundId id, bool loop = true);
    void StopMusic();
}
