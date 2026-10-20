namespace AssetStudio
{
    /// <summary>
    /// Particle renderers are not parsed by stock AssetStudio. Only the Renderer base (materials) is
    /// read here, which is enough to find the materials and textures an effect uses.
    /// </summary>
    public sealed class ParticleSystemRenderer : Renderer
    {
        public ParticleSystemRenderer(ObjectReader reader) : base(reader) { }
    }
}
