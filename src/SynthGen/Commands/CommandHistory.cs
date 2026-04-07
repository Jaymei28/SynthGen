using System;
using System.Collections.Generic;

namespace SynthGen.Commands;

/// <summary>
/// Manages the Undo and Redo stacks for the application.
/// </summary>
public class CommandHistory
{
    private readonly Stack<ICommand> _undoStack = new();
    private readonly Stack<ICommand> _redoStack = new();
    
    public int MaxHistory { get; set; } = 100;

    public event Action? OnHistoryChanged;

    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;

    /// <summary>
    /// Pushes a new command onto the Undo stack and clears the Redo stack.
    /// Does not automatically execute the command, as it's assumed the user
    /// has already performed the action (e.g. via ImGui drag).
    /// </summary>
    public void Push(ICommand command)
    {
        _undoStack.Push(command);
        _redoStack.Clear();

        // Enforce max history
        if (_undoStack.Count > MaxHistory)
        {
            // Reverse stack to array, truncate, rebuild. Expensive but happens rarely.
            var arr = _undoStack.ToArray();
            _undoStack.Clear();
            for (int i = MaxHistory - 1; i >= 0; i--)
            {
                _undoStack.Push(arr[i]);
            }
        }

        OnHistoryChanged?.Invoke();
    }
    
    /// <summary>
    /// Executes a command and then pushes it to the stack.
    /// </summary>
    public void Execute(ICommand command)
    {
        command.Execute();
        Push(command);
    }

    public void Undo()
    {
        if (_undoStack.Count == 0) return;
        
        var cmd = _undoStack.Pop();
        cmd.Undo();
        _redoStack.Push(cmd);
        
        OnHistoryChanged?.Invoke();
    }

    public void Redo()
    {
        if (_redoStack.Count == 0) return;
        
        var cmd = _redoStack.Pop();
        // Redo is effectively Execute for ActionCommand
        cmd.Execute();
        _undoStack.Push(cmd);
        
        OnHistoryChanged?.Invoke();
    }
}
