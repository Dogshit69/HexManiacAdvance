﻿using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace HavenSoft.Gen3Hex.Model {
   /// <summary>
   /// Represents a history of changes that can undo / redo.
   /// The change can be reperesented by any class with an empty constructor.
   /// New change objects will be created automatically as needed.
   /// The user is responsible for using the change object to revert a change via the constructor delegate call.
   /// The user is responsible for converting from a backward change object to a forward change (redo) object.
   /// The user is responsible for assigning boundaries between changes by calling ChangeCompleted.
   /// </summary>
   public class ChangeHistory<T> where T : class, new() {
      private readonly Func<T, T> revert;
      private readonly StubCommand undo, redo;
      private readonly Stack<T>
         undoStack = new Stack<T>(),
         redoStack = new Stack<T>();

      private bool revertInProgress;
      private T currentChange;

      public ICommand Undo => undo;
      public ICommand Redo => redo;
      public T CurrentChange {
         get {
            VerifyRevertNotInProgress();

            if (redoStack.Count > 0) {
               redoStack.Clear();
               redo.CanExecuteChanged.Invoke(redo, EventArgs.Empty);
            }

            if (currentChange == null) {
               currentChange = new T();
               if (undoStack.Count == 0) undo.CanExecuteChanged.Invoke(undo, EventArgs.Empty);
            }

            return currentChange;
         }
      }

      public ChangeHistory(Func<T, T> revertChange) {
         revert = revertChange;
         undo = new StubCommand {
            Execute = arg => UndoExecuted(),
            CanExecute = arg => undoStack.Count > 0 || currentChange != null,
         };
         redo = new StubCommand {
            Execute = arg => RedoExecuted(),
            CanExecute = arg => redoStack.Count > 0,
         };
      }

      public void ChangeCompleted() {
         if (currentChange == null) return;
         VerifyRevertNotInProgress();
         undoStack.Push(currentChange);
         currentChange = null;
      }

      private void UndoExecuted() {
         ChangeCompleted();
         if (undoStack.Count == 0) return;
         revertInProgress = true;

         using (CreateRevertScope()) {
            var originalChange = undoStack.Pop();
            if (undoStack.Count == 0) undo.CanExecuteChanged.Invoke(undoStack, EventArgs.Empty);
            var reverseChange = revert(originalChange);
            redoStack.Push(reverseChange);
            if (redoStack.Count == 1) redo.CanExecuteChanged.Invoke(redo, EventArgs.Empty);
         }
      }

      private void RedoExecuted() {
         if (redoStack.Count == 0) return;

         using (CreateRevertScope()) {
            var reverseChange = redoStack.Pop();
            if (redoStack.Count == 0) redo.CanExecuteChanged.Invoke(redoStack, EventArgs.Empty);
            var originalChange = revert(reverseChange);
            undoStack.Push(originalChange);
            if (undoStack.Count == 1) undo.CanExecuteChanged.Invoke(undo, EventArgs.Empty);
         }
      }

      private void VerifyRevertNotInProgress([CallerMemberName]string caller = null) {
         if (!revertInProgress) return;
         throw new InvalidOperationException($"Cannot execute member {caller} while a revert is in progress.");
      }

      private IDisposable CreateRevertScope() {
         revertInProgress = true;
         var stub = new StubDisposable { Dispose = () => revertInProgress = false };
         return stub;
      }
   }
}
