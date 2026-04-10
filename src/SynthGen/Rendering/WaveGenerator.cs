using Silk.NET.OpenGL;
using System.Numerics;
using SynthGen.Physics;

namespace SynthGen.Rendering;

/// <summary>
/// Handles the GPU compute pipeline for wave spectra generation and FFT.
/// Ported from GodotOceanWaves logic.
/// </summary>
public class WaveGenerator : IDisposable
{
    private readonly GL _gl;
    private readonly int _mapSize;
    private readonly int _numCascades;

    // Displacement and Normal maps (Texture2DArray: layer per cascade)
    public uint DisplacementMap { get; private set; }
    public uint NormalMap { get; private set; }
    
    // Internal textures/buffers
    private uint _spectrumTex;
    private uint _butterflyBuffer;
    private uint _fftBuffer;

    // Shaders
    private Shader _spectraCompute;
    private Shader _spectraModulate;
    private Shader _fftButterfly;
    private Shader _fftCompute;
    private Shader _fftTranspose;
    private Shader _fftUnpack;

    private bool _initialized = false;

    public WaveGenerator(GL gl, int mapSize = 1024, int numCascades = 2)
    {
        _gl = gl;
        _mapSize = mapSize;
        _numCascades = numCascades;

        // 1. Create Texture Arrays
        DisplacementMap = CreateTextureArray(mapSize, InternalFormat.Rgba16f);
        NormalMap = CreateTextureArray(mapSize, InternalFormat.Rgba16f);
        _spectrumTex = CreateTextureArray(mapSize, InternalFormat.Rgba16f);

        // 2. Create SSBOs
        int numStages = (int)Math.Log2(mapSize);
        _butterflyBuffer = CreateBuffer(numStages * mapSize * 16); // vec4[]
        _fftBuffer = CreateBuffer(numCascades * mapSize * mapSize * 4 * 2 * 8); // vec2[NUM_SPECTRA * 2] (packed)

        // 3. Compile Shaders
        _spectraCompute = new Shader(_gl, ShaderSources.SPECTRA_COMPUTE);
        _spectraModulate = new Shader(_gl, ShaderSources.SPECTRA_MODULATE);
        _fftButterfly = new Shader(_gl, ShaderSources.FFT_BUTTERFLY);
        _fftCompute = new Shader(_gl, ShaderSources.FFT_COMPUTE);
        _fftTranspose = new Shader(_gl, ShaderSources.FFT_TRANSPOSE);
        _fftUnpack = new Shader(_gl, ShaderSources.FFT_UNPACK);

        // 4. Precompute Butterfly Factors
        PrecomputeButterfly();
        _initialized = true;
    }

    private unsafe uint CreateTextureArray(int size, InternalFormat format)
    {
        uint tex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2DArray, tex);
        _gl.TexStorage3D(TextureTarget.Texture2DArray, 1, (SizedInternalFormat)format, (uint)size, (uint)size, (uint)_numCascades);
        _gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        _gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        _gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
        _gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);
        return tex;
    }

    private unsafe uint CreateBuffer(int size)
    {
        uint buf = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, buf);
        _gl.BufferData(BufferTargetARB.ShaderStorageBuffer, (nuint)size, null, BufferUsageARB.StaticDraw);
        return buf;
    }

    private void PrecomputeButterfly()
    {
        _fftButterfly.Use();
        _fftButterfly.SetInt("uMapSize", _mapSize);
        _gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 0, _butterflyBuffer);
        
        int numStages = (int)Math.Log2(_mapSize);
        _gl.DispatchCompute((uint)(_mapSize / 64), (uint)numStages, 1);
        _gl.MemoryBarrier(MemoryBarrierMask.ShaderStorageBarrierBit);
    }

    public void Update(float time, OceanConfig cfg)
    {
        if (!_initialized) return;

        // Update Cascade 1
        UpdateCascade(0, time, cfg);
    }

    private void UpdateCascade(uint index, float time, OceanConfig cfg)
    {
        // 1. Spectra Compute (Optional frequency re-gen, but here we do it every time for now or on change)
        // In the real version we'd only do this when parameters change.
        _spectraCompute.Use();
        _spectraCompute.SetInt("pc.cascade_index", (int)index);
        _spectraCompute.SetVec2("pc.tile_length", cfg.TileLength);
        _spectraCompute.SetFloat("pc.alpha", 0.0081f); // Ported JONSWAP alpha
        _spectraCompute.SetFloat("pc.peak_frequency", 9.81f / (cfg.WindSpeed + 1e-6f));
        _spectraCompute.SetFloat("pc.wind_speed", cfg.WindSpeed);
        _spectraCompute.SetFloat("pc.angle", cfg.WindDirection * (MathF.PI / 180f));
        _spectraCompute.SetFloat("pc.depth", 20.0f);
        _spectraCompute.SetFloat("pc.swell", cfg.Swell);
        _spectraCompute.SetFloat("pc.detail", cfg.Detail);
        _spectraCompute.SetFloat("pc.spread", cfg.Spread);
        _spectraCompute.SetVec2("pc.seed", new Vector2(123, 456));

        _gl.BindImageTexture(0, _spectrumTex, 0, true, 0, BufferAccessARB.WriteOnly, InternalFormat.Rgba16f);
        _gl.DispatchCompute((uint)(_mapSize / 16), (uint)(_mapSize / 16), 1);
        _gl.MemoryBarrier(MemoryBarrierMask.ShaderImageAccessBarrierBit);

        // 2. Modulate
        _spectraModulate.Use();
        _spectraModulate.SetVec2("pc.tile_length", cfg.TileLength);
        _spectraModulate.SetFloat("pc.depth", 20.0f);
        _spectraModulate.SetFloat("pc.time", time * cfg.TimeScale);
        _spectraModulate.SetInt("pc.cascade_index", (int)index);
        _spectraModulate.SetInt("pc.mapSize", _mapSize);
        
        _gl.BindImageTexture(0, _spectrumTex, 0, true, 0, BufferAccessARB.ReadOnly, InternalFormat.Rgba16f);
        _gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 1, _fftBuffer);
        _gl.DispatchCompute((uint)(_mapSize / 16), (uint)(_mapSize / 16), 1);
        _gl.MemoryBarrier(MemoryBarrierMask.ShaderStorageBarrierBit);

        // 3. FFT Horizontal
        _fftCompute.Use();
        _fftCompute.SetInt("uCascadeIndex", (int)index);
        _fftCompute.SetInt("uMapSize", _mapSize);
        _gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 0, _butterflyBuffer);
        _gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 1, _fftBuffer);
        
        // Dispatch across all 4 packed spectra
        _gl.DispatchCompute((uint)(_mapSize), 1, 4); // x=cols, y=rows, z=spectra
        _gl.MemoryBarrier(MemoryBarrierMask.ShaderStorageBarrierBit);

        // 4. Transpose
        _fftTranspose.Use();
        _fftTranspose.SetInt("uCascadeIndex", (int)index);
        _fftTranspose.SetInt("uMapSize", _mapSize);
        _gl.DispatchCompute((uint)(_mapSize / 32), (uint)(_mapSize / 32), 4);
        _gl.MemoryBarrier(MemoryBarrierMask.ShaderStorageBarrierBit);

        // 5. FFT Vertical (actually same kernel, input was transposed)
        _fftCompute.Use();
        _gl.DispatchCompute((uint)(_mapSize), 1, 4);
        _gl.MemoryBarrier(MemoryBarrierMask.ShaderStorageBarrierBit);

        // 6. Unpack
        _fftUnpack.Use();
        _fftUnpack.SetInt("pc.cascade_index", (int)index);
        _fftUnpack.SetInt("pc.mapSize", _mapSize);
        _fftUnpack.SetFloat("pc.whitecap", cfg.Whitecap);
        _fftUnpack.SetFloat("pc.foam_grow_rate", 0.1f * cfg.FoamAmount);
        _fftUnpack.SetFloat("pc.foam_decay_rate", 0.05f);

        _gl.BindImageTexture(0, DisplacementMap, 0, true, 0, BufferAccessARB.WriteOnly, InternalFormat.Rgba16f);
        _gl.BindImageTexture(1, NormalMap, 0, true, 0, BufferAccessARB.ReadWrite, InternalFormat.Rgba16f);
        _gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 2, _fftBuffer);

        _gl.DispatchCompute((uint)(_mapSize / 16), (uint)(_mapSize / 16), 1);
        _gl.MemoryBarrier(MemoryBarrierMask.ShaderImageAccessBarrierBit | MemoryBarrierMask.TextureFetchBarrierBit);
    }

    public void Dispose()
    {
        _gl.DeleteTexture(DisplacementMap);
        _gl.DeleteTexture(NormalMap);
        _gl.DeleteTexture(_spectrumTex);
        _gl.DeleteBuffer(_butterflyBuffer);
        _gl.DeleteBuffer(_fftBuffer);
        _spectraCompute.Dispose();
        _spectraModulate.Dispose();
        _fftButterfly.Dispose();
        _fftCompute.Dispose();
        _fftTranspose.Dispose();
        _fftUnpack.Dispose();
    }
}
