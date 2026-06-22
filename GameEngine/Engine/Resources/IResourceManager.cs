namespace AsteroidsEngine.Engine.Resources;

/// <summary>
/// Asset cache abstraction — the asset half of the Platform Abstraction Layer.
/// The engine refers to assets by opaque id; each backend decodes and owns the
/// native handle (SkiaSharp SKImage, GDI+ Bitmap, ...). Implementations are
/// expected to cache by path and reference-count via Release.
/// </summary>
public interface IResourceManager : IDisposable
{
    ImageId LoadImage(string path);
    SoundId LoadSound(string path);

    void Release(ImageId id);
    void Release(SoundId id);
}
