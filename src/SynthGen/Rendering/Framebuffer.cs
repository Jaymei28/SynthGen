using Silk.NET.OpenGL;

namespace SynthGen.Rendering;

/// <summary>
/// Off-screen framebuffer with color + depth attachments.
/// </summary>
public class Framebuffer : IDisposable
{
    private readonly GL _gl;
    public uint FBO { get; private set; }
    public uint ColorTexture { get; private set; }
    public uint DepthRBO { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }

    public Framebuffer(GL gl, int width, int height)
    {
        _gl = gl;
        Width = width;
        Height = height;
        Create();
    }

    private unsafe void Create()
    {
        FBO = _gl.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, FBO);

        // Color attachment
        ColorTexture = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, ColorTexture);
        _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)Width, (uint)Height, 0,
                       PixelFormat.Rgba, PixelType.UnsignedByte, null);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                                 TextureTarget.Texture2D, ColorTexture, 0);

        // Depth renderbuffer
        DepthRBO = _gl.GenRenderbuffer();
        _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, DepthRBO);
        _gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.Depth24Stencil8, (uint)Width, (uint)Height);
        _gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthStencilAttachment,
                                    RenderbufferTarget.Renderbuffer, DepthRBO);

        var status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status != GLEnum.FramebufferComplete)
            Console.WriteLine($"[Framebuffer] Incomplete: {status}");

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    public void Bind()
    {
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, FBO);
        _gl.Viewport(0, 0, (uint)Width, (uint)Height);
    }

    public void Unbind()
    {
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    public unsafe byte[] ReadPixels()
    {
        var pixels = new byte[Width * Height * 4];
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, FBO);
        fixed (byte* ptr = pixels)
        {
            _gl.ReadPixels(0, 0, (uint)Width, (uint)Height, PixelFormat.Rgba, PixelType.UnsignedByte, ptr);
        }
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        return pixels;
    }

    public void Resize(int w, int h)
    {
        if (w == Width && h == Height) return;
        Dispose();
        Width = w;
        Height = h;
        Create();
    }

    public void Dispose()
    {
        _gl.DeleteFramebuffer(FBO);
        _gl.DeleteTexture(ColorTexture);
        _gl.DeleteRenderbuffer(DepthRBO);
    }
}
