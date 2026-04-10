using Silk.NET.OpenGL;

namespace SynthGen.Rendering;

/// <summary>
/// Wraps an OpenGL shader program (vertex + fragment).
/// </summary>
public class Shader : IDisposable
{
    private readonly GL _gl;
    public uint Handle { get; private set; }

    public Shader(GL gl, string vertexSource, string fragmentSource)
    {
        _gl = gl;
        uint vs = CompileShader(ShaderType.VertexShader, vertexSource);
        uint fs = CompileShader(ShaderType.FragmentShader, fragmentSource);

        Handle = _gl.CreateProgram();
        _gl.AttachShader(Handle, vs);
        _gl.AttachShader(Handle, fs);
        _gl.LinkProgram(Handle);
        _gl.GetProgram(Handle, ProgramPropertyARB.LinkStatus, out int status);
        if (status == 0)
        {
            string log = _gl.GetProgramInfoLog(Handle);
            throw new Exception($"Shader link error: {log}");
        }
        _gl.DeleteShader(vs);
        _gl.DeleteShader(fs);
    }

    public Shader(GL gl, string computeSource)
    {
        _gl = gl;
        uint cs = CompileShader(ShaderType.ComputeShader, computeSource);

        Handle = _gl.CreateProgram();
        _gl.AttachShader(Handle, cs);
        _gl.LinkProgram(Handle);
        _gl.GetProgram(Handle, ProgramPropertyARB.LinkStatus, out int status);
        if (status == 0)
        {
            string log = _gl.GetProgramInfoLog(Handle);
            throw new Exception($"Compute shader link error: {log}");
        }
        _gl.DeleteShader(cs);
    }

    private uint CompileShader(ShaderType type, string source)
    {
        uint shader = _gl.CreateShader(type);
        _gl.ShaderSource(shader, source);
        _gl.CompileShader(shader);
        _gl.GetShader(shader, ShaderParameterName.CompileStatus, out int status);
        if (status == 0)
        {
            string log = _gl.GetShaderInfoLog(shader);
            System.IO.File.WriteAllText("shader_error.log", $"Shader compile error ({type}):\n{log}");
            Console.Error.WriteLine($"Shader compile error ({type}):\n{log}");
            throw new Exception($"Shader compile error ({type}): {log}");
        }
        return shader;
    }

    public void Use() => _gl.UseProgram(Handle);

    public void SetInt(string name, int value)
    {
        int loc = _gl.GetUniformLocation(Handle, name);
        if (loc >= 0) _gl.Uniform1(loc, value);
    }

    public void SetFloat(string name, float value)
    {
        int loc = _gl.GetUniformLocation(Handle, name);
        if (loc >= 0) _gl.Uniform1(loc, value);
    }

    public void SetVec3(string name, System.Numerics.Vector3 v)
    {
        int loc = _gl.GetUniformLocation(Handle, name);
        if (loc >= 0) _gl.Uniform3(loc, v.X, v.Y, v.Z);
    }

    public void SetVec2(string name, System.Numerics.Vector2 v)
    {
        int loc = _gl.GetUniformLocation(Handle, name);
        if (loc >= 0) _gl.Uniform2(loc, v.X, v.Y);
    }

    public void SetVec4(string name, System.Numerics.Vector4 v)
    {
        int loc = _gl.GetUniformLocation(Handle, name);
        if (loc >= 0) _gl.Uniform4(loc, v.X, v.Y, v.Z, v.W);
    }

    public unsafe void SetMat4(string name, System.Numerics.Matrix4x4 mat)
    {
        int loc = _gl.GetUniformLocation(Handle, name);
        if (loc >= 0) _gl.UniformMatrix4(loc, 1, false, (float*)&mat);
    }

    public unsafe void SetMat3(string name, System.Numerics.Matrix4x4 mat)
    {
        int loc = _gl.GetUniformLocation(Handle, name);
        if (loc >= 0)
        {
            float[] m3 = {
                mat.M11, mat.M12, mat.M13,
                mat.M21, mat.M22, mat.M23,
                mat.M31, mat.M32, mat.M33
            };
            fixed(float* ptr = m3)
                _gl.UniformMatrix3(loc, 1, false, ptr);
        }
    }

    public void Dispose()
    {
        _gl.DeleteProgram(Handle);
    }
}
