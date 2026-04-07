namespace SynthGen.Commands;

public interface ICommand
{
    void Execute();
    void Undo();
}
