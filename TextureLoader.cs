namespace OriathHub.Plugins.HealthBars
{
    using System;
    using System.Collections.Generic;
    using System.IO;

    /// <summary>
    ///     Loads and cleans up the textures.
    /// </summary>
    public class TextureLoader
    {
        private readonly Dictionary<string, (IntPtr, int w, int h)> loadedTextures = new();

        /// <summary>
        ///     Gets all the keys of the loaded textures.
        /// </summary>
        public List<string> TextureKeys => new(this.loadedTextures.Keys);

        /// <summary>
        ///     Gets the total number of loaded textures.
        /// </summary>
        public int TotalTexturesLoaded => this.loadedTextures.Count;

        /// <summary>
        ///     Unloads all the textures.
        /// </summary>
        /// <param name="texturesPath">path to texture folder.</param>
        public void cleanup(string texturesPath)
        {
            // Drop our entry unconditionally. RemoveImage removes the overlay's own entry first and
            // then returns whether the *renderer* still held the texture, so a false does not mean
            // "still loaded" — keeping our entry on a false left this dictionary and the overlay out
            // of sync, and the next Load() then threw ArgumentException on the duplicate key.
            foreach (var filename in this.loadedTextures.Keys)
            {
                Core.Overlay.RemoveImage(Path.Join(texturesPath, filename));
            }

            this.loadedTextures.Clear();
        }

        /// <summary>
        ///     Loads all the textures.
        /// </summary>
        /// <param name="texturesPath">Path to texture folder.</param>
        public void Load(string texturesPath)
        {
            if (Directory.Exists(texturesPath))
            {
                foreach (var pathname in Directory.EnumerateFiles(texturesPath))
                {
                    var filename = Path.GetFileName(pathname);
                    Core.Overlay.AddOrGetImagePointer(pathname, false, out var handle, out var w, out var h);

                    // Indexer, not Add: a stale entry must not turn a reload into an
                    // ArgumentException that escapes OnEnable and leaves the plugin enabled but dead.
                    this.loadedTextures[filename] = (handle, (int)w, (int)h);
                }
            }
        }

        /// <summary>
        ///     Gets the texture along with width and height.
        /// </summary>
        /// <param name="key">texture identifier</param>
        /// <returns></returns>
        public (IntPtr, int w, int h) GetTexture(string key) => this.loadedTextures[key];
    }
}
