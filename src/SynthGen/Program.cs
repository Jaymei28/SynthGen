using System.Windows.Forms;

namespace SynthGen;

class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        try
        {
            var app = new App.Application();
            app.Run();
        }
        catch (Exception ex)
        {
            // Write a crash log next to the EXE
            string logPath = Path.Combine(AppContext.BaseDirectory, "crash_log.txt");
            string msg = $"[{DateTime.Now}] SynthGen Crash\n{ex}\n\n";
            try { File.AppendAllText(logPath, msg); } catch { }

            // Show a visible error dialog
            MessageBox.Show(
                $"SynthGen failed to start:\n\n{ex.Message}\n\nDetails saved to:\n{logPath}",
                "SynthGen Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }
}
