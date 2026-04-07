using System;

namespace SynthGen.Commands;

/// <summary>
/// A generic command that executes an action, and undoes it using a separate action.
/// Useful for encapsulating state snaphots captured during UI interactions.
/// </summary>
public class ActionCommand : ICommand
{
    private readonly Action _undoAction;
    private readonly Action _redoAction;
    private readonly Action? _onExecute;

    public ActionCommand(Action undoAction, Action redoAction, Action? onExecute = null)
    {
        _undoAction = undoAction;
        _redoAction = redoAction;
        _onExecute = onExecute ?? redoAction;
    }

    public void Execute()
    {
        _onExecute?.Invoke();
    }

    public void Undo()
    {
        _undoAction.Invoke();
    }
}
