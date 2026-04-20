namespace SynthGen;

class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        var app = new App.Application();
        app.Run();
    }
}
