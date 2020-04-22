﻿using HavenSoft.HexManiac.Core.Models.Runs;
using HavenSoft.HexManiac.Core.ViewModels.DataFormats;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace HavenSoft.HexManiac.Core.Models.Code {
   public class ScriptParser {
      private readonly IReadOnlyList<ScriptLine> engine;

      public event EventHandler<string> CompileError;

      public ScriptParser(IReadOnlyList<ScriptLine> engine) => this.engine = engine;

      public int GetScriptSegmentLength(IDataModel model, int address) => engine.GetScriptSegmentLength(model, address);

      public string Parse(IDataModel data, int start, int length) {
         var builder = new StringBuilder();
         foreach (var line in Decompile(data, start, length)) builder.AppendLine(line);
         return builder.ToString();
      }

      public List<int> CollectScripts(IDataModel model, int address) {
         if (address < 0 || address >= model.Count) return new List<int>();
         var scripts = new List<int> { address };

         for (int i = 0; i < scripts.Count; i++) {
            address = scripts[i];
            int length = 0;
            while (true) {
               var line = engine.GetMatchingLine(model, address + length);
               if (line?.IsEndingCommand ?? true) break;
               length += line.LineCode.Count;
               foreach (var arg in line.Args) {
                  if (arg.Type == ArgType.Pointer) {
                     var destination = model.ReadPointer(address + length);
                     if (destination >= 0 && destination < model.Count &&
                        line.PointsToNextScript &&
                        !scripts.Contains(destination)
                     ) {
                        scripts.Add(destination);
                     }
                  }
                  length += arg.Length;
               }
            }
         }

         return scripts;
      }

      public int FindLength(IDataModel model, int address) {
         int length = 0;

         while (true) {
            var line = engine.GetMatchingLine(model, address + length);
            if (line == null) break;
            length += line.CompiledByteLength;
            if (line.IsEndingCommand) break;
         }

         return length;
      }

      // TODO refactor to rely on CollectScripts rather than duplicate code
      public void FormatScript(ModelDelta token, IDataModel model, int address) {
         var processed = new List<int>();
         var toProcess = new List<int> { address };
         while (toProcess.Count > 0) {
            address = toProcess.Last();
            toProcess.RemoveAt(toProcess.Count - 1);
            if (processed.Contains(address)) continue;
            var existingRun = model.GetNextRun(address);
            if (!(existingRun is XSERun && existingRun.Start == address)) {
               model.ObserveAnchorWritten(token, string.Empty, new XSERun(address));
            }
            int length = 0;
            while (true) {
               var line = engine.GetMatchingLine(model, address + length);
               if (line == null) break;
               length += line.LineCode.Count;
               foreach (var arg in line.Args) {
                  if (arg.Type != ArgType.Pointer) {
                     length += arg.Length;
                     continue;
                  }
                  var destination = model.ReadPointer(address + length);
                  if (destination >= 0 && destination < model.Count) {
                     model.ClearFormat(token, address + length, 4);
                     model.ObserveRunWritten(token, new PointerRun(address + length));
                     if (line.PointsToNextScript) toProcess.Add(destination);
                     if (line.PointsToText) {
                        var destinationLength = PCSString.ReadString(model, destination, false);
                        if (destinationLength > 0) model.ObserveRunWritten(token, new PCSRun(model, destination, destinationLength));
                     } else if (line.PointsToMovement) {
                        WriteMovementStream(model, token, destination, address + length);
                     }
                  }
                  length += arg.Length;
               }
               if (line.IsEndingCommand) break;
            }
            processed.Add(address);
         }
      }

      private void WriteMovementStream(IDataModel model, ModelDelta token, int start, int source) {
         if (TableStreamRun.TryParseTableStream(model, start, new[] { source }, string.Empty, "[move.movementtypes]!FE", null, out var tsRun)) {
            model.ObserveRunWritten(token, tsRun);
         }
      }

      public byte[] Compile(ModelDelta token, IDataModel model, ref string script, out IReadOnlyList<(int originalLocation, int newLocation)> movedData) {
         movedData = new List<(int, int)>();
         var lines = script.Split(new[] { Environment.NewLine }, StringSplitOptions.None)
            .Select(line => line.Split('#').First().Trim())
            .Where(line => !string.IsNullOrEmpty(line))
            .ToArray();
         var result = new List<byte>();
         int streamLocation = -1, streamPointerLocation = -1;

         for (var i = 0; i < lines.Length; i++) {
            var line = lines[i];
            if (line == "{") {
               var streamStart = i + 1;
               var indentCount = 1;
               i += 2;
               while (indentCount > 0) {
                  line = lines[i];
                  if (line == "{") indentCount += 1;
                  if (line == "}") indentCount -= 1;
                  i += 1;
                  if (i == lines.Length) break;
               }
               i -= 1;
               var streamEnd = i;
               var stream = lines.Skip(streamStart).Take(streamEnd - streamStart + 1).Aggregate((a, b) => a + Environment.NewLine + b);

               // Let the stream run handle updating itself based on the stream content.
               if (streamLocation >= 0 && streamPointerLocation >= 0) {
                  var streamRun = model.GetNextRun(streamLocation) as IStreamRun;
                  if (streamRun != null && streamRun.Start == streamLocation) {
                     streamRun = streamRun.DeserializeRun(stream, token);
                     // alter script content and compiled byte location based on stream move
                     if (streamRun.Start != streamLocation) {
                        script = script.Replace(streamLocation.ToString("X6"), streamRun.Start.ToString("X6"));
                        result[streamPointerLocation + 0] = (byte)(streamLocation >> 0);
                        result[streamPointerLocation + 1] = (byte)(streamLocation >> 8);
                        result[streamPointerLocation + 2] = (byte)(streamLocation >> 16);
                        result[streamPointerLocation + 3] = (byte)((streamLocation >> 24) + 0x08);
                        ((List<(int, int)>)movedData).Add((streamLocation, streamRun.Start));
                     }
                  }
               }
               continue;
            }
            streamLocation = -1; streamPointerLocation = -1;
            foreach (var command in engine) {
               if (!(line + " ").StartsWith(command.LineCommand + " ")) continue;
               var currentSize = result.Count;

               if (line.Contains("<??????>")) {
                  int newAddress = -1;
                  if (command.PointsToMovement) {
                     newAddress = model.FindFreeSpace(0, 0x10);
                     token.ChangeData(model, newAddress, 0xFE);
                  } else if (command.PointsToText) {
                     newAddress = model.FindFreeSpace(0, 0x10);
                     token.ChangeData(model, newAddress, 0xFF);
                  } else if (command.PointsToNextScript) {
                     newAddress = model.FindFreeSpace(0, 0x10);
                     token.ChangeData(model, newAddress, 0x02);
                  }
                  if (newAddress != -1) {
                     line = line.Replace("<??????>", $"<{newAddress:X6}>");
                     script = script.Replace("<??????>", $"<{newAddress:X6}>");
                  }
               }

               var error = command.Compile(model, line, out var code);
               if (error == null) {
                  result.AddRange(code);
               } else {
                  CompileError?.Invoke(this, i + ": " + error);
                  return null;
               }
               if (command.PointsToMovement || command.PointsToText) {
                  var pointerOffset = command.Args.Until(arg => arg.Type == ArgType.Pointer).Sum(arg => arg.Length) + command.LineCode.Count;
                  var destination = result.ReadMultiByteValue(currentSize + pointerOffset, 4) - 0x8000000;
                  if (destination >= 0) {
                     streamPointerLocation = currentSize + pointerOffset;
                     streamLocation = destination;
                  }
               }
            }
         }

         if (result.Count == 0) result.Add(0x02); // end
         return result.ToArray();
      }

      private string[] Decompile(IDataModel data, int index, int length) {
         var results = new List<string>();
         while (length > 0) {
            var line = engine.FirstOrDefault(option => Enumerable.Range(0, option.LineCode.Count).All(i => data[index + i] == option.LineCode[i]));
            if (line == null) {
               results.Add($".raw {data[index]:X2}");
               index += 1;
               length -= 1;
            } else {
               results.Add(line.Decompile(data, index));
               index += line.CompiledByteLength;
               length -= line.CompiledByteLength;
               if (line.IsEndingCommand) break;
            }
         }
         return results.ToArray();
      }
   }

   public interface IScriptLine {
      IReadOnlyList<ScriptArg> Args { get; }
      IReadOnlyList<byte> LineCode { get; }
      string LineCommand { get; }
      int CompiledByteLength { get; }
      bool IsEndingCommand { get; }
      bool PointsToNextScript { get; }
      bool PointsToText { get; }
      bool PointsToMovement { get; }

      bool Matches(IReadOnlyList<byte> data, int index);
      string Compile(IDataModel model, string scriptLine, out byte[] result);
      string Decompile(IDataModel data, int start);
   }

   public class ScriptLine : IScriptLine {
      public const string Hex = "0123456789ABCDEF";
      public IReadOnlyList<ScriptArg> Args { get; }
      public IReadOnlyList<byte> LineCode { get; }
      public string LineCommand { get; }
      public int CompiledByteLength { get; }

      private static readonly byte[] endCodes = new byte[] { 0x02, 0x03, 0x05, 0x08, 0x0A, 0x0C, 0x0D };
      public bool IsEndingCommand { get; }
      public bool PointsToNextScript => LineCode.Count == 1 && LineCode[0].IsAny<byte>(4, 5, 6, 7);
      public bool PointsToText => LineCode.Count == 1 && LineCode[0].IsAny<byte>(0x0F, 0x67);
      public bool PointsToMovement => LineCode.Count == 1 && LineCode[0].IsAny<byte>(0x4F, 0x50);

      public ScriptLine(string engineLine) {
         var tokens = engineLine.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
         var lineCode = new List<byte>();
         var args = new List<ScriptArg>();

         foreach (var token in tokens) {
            if (token.Length == 2 && token.All(Hex.Contains)) {
               lineCode.Add(byte.Parse(token, NumberStyles.HexNumber));
               continue;
            }
            if ("<> : .".Split(' ').Any(token.Contains)) {
               args.Add(new ScriptArg(token));
               continue;
            }
            LineCommand = token;
         }

         LineCode = lineCode;
         Args = args;
         CompiledByteLength = LineCode.Count + Args.Sum(arg => arg.Length);
         IsEndingCommand = LineCode.Count == 1 && endCodes.Contains(LineCode[0]);
      }

      public bool Matches(IReadOnlyList<byte> data, int index) {
         if (index + LineCode.Count >= data.Count) return false;
         return Enumerable.Range(0, LineCode.Count).All(i => data[index + i] == LineCode[i]);
      }

      public string Compile(IDataModel model, string scriptLine, out byte[] result) {
         result = null;
         var tokens = scriptLine.Split(new[] { " " }, StringSplitOptions.None);
         if (tokens[0] != LineCommand) throw new ArgumentException($"Command {LineCommand} was expected, but received {tokens[0]} instead.");
         if (Args.Count != tokens.Length - 1) {
            return $"Command {LineCommand} expects {Args.Count} arguments, but received {tokens.Length - 1} instead.";
         }
         var results = new List<byte>(LineCode);
         for (int i = 0; i < Args.Count; i++) {
            var token = tokens[i + 1];
            if (Args[i].Type == ArgType.Byte) {
               results.Add((byte)Args[i].Convert(model, token));
            } else if (Args[i].Type == ArgType.Short) {
               var value = Args[i].Convert(model, token);
               results.Add((byte)value);
               results.Add((byte)(value >> 8));
            } else if (Args[i].Type == ArgType.Word) {
               var value = Args[i].Convert(model, token);
               results.Add((byte)value);
               results.Add((byte)(value >> 0x8));
               results.Add((byte)(value >> 0x10));
               results.Add((byte)(value >> 0x18));
            } else if (Args[i].Type == ArgType.Pointer) {
               int value;
               if (token.StartsWith("<")) {
                  if (!token.EndsWith(">")) {
                     return "Unmatched <>";
                  }
                  token = token.Substring(1, token.Length - 2);
                  if (!int.TryParse(token, NumberStyles.HexNumber, CultureInfo.CurrentCulture, out value)) {
                     return $"Unable to parse {token} as a hex number.";
                  }
                  value += 0x8000000;
               } else {
                  value = int.Parse(token, NumberStyles.HexNumber);
               }
               results.Add((byte)value);
               results.Add((byte)(value >> 0x8));
               results.Add((byte)(value >> 0x10));
               results.Add((byte)(value >> 0x18));
            } else {
               throw new NotImplementedException();
            }
         }
         result = results.ToArray();
         return null;
      }

      public string Decompile(IDataModel data, int start) {
         for (int i = 0; i < LineCode.Count; i++) {
            if (LineCode[i] != data[start + i]) throw new ArgumentException($"Data at {start:X6} does not match the {LineCommand} command.");
         }
         start += LineCode.Count;
         var builder = new StringBuilder(LineCommand);
         int lastAddress = -1;
         foreach (var arg in Args) {
            builder.Append(" ");
            if (arg.Type == ArgType.Byte) builder.Append($"{arg.Convert(data, data[start])}");
            if (arg.Type == ArgType.Short) builder.Append($"{arg.Convert(data, data.ReadMultiByteValue(start, 2))}");
            if (arg.Type == ArgType.Word) builder.Append($"{arg.Convert(data, data.ReadMultiByteValue(start, 4))}");
            if (arg.Type == ArgType.Pointer) {
               var address = data.ReadMultiByteValue(start, 4);
               if (address < 0x8000000) {
                  builder.Append(address.ToString("X6"));
               } else {
                  address -= 0x8000000;
                  builder.Append($"<{address:X6}>");
                  lastAddress = address;
               }
            }
            start += arg.Length;
         }
         if (PointsToText || PointsToMovement) {
            var stream = data.GetNextRun(lastAddress) as IStreamRun;
            if (stream != null) {
               builder.AppendLine();
               builder.AppendLine("{");
               builder.AppendLine(stream.SerializeRun());
               builder.Append("}");
            }
         }
         return builder.ToString();
      }

      public static string ReadString(IReadOnlyList<byte> data, int start) {
         var length = PCSString.ReadString(data, start, true);
         return PCSString.Convert(data, start, length);
      }
   }

   public class ScriptArg {
      public ArgType Type { get; }
      public string Name { get; }
      public string EnumTableName { get; }
      public int Length { get; }
      public ScriptArg(string token) {
         if (token.Contains("<>")) {
            (Type, Length) = (ArgType.Pointer, 4);
            Name = token.Split(new[] { "<>" }, StringSplitOptions.None).First();
         } else if (token.Contains(".")) {
            (Type, Length) = (ArgType.Byte, 1);
            Name = token.Split('.').First();
            EnumTableName = token.Split('.').Last();
         } else if (token.Contains("::")) {
            (Type, Length) = (ArgType.Word, 4);
            Name = token.Split(new[] { "::" }, StringSplitOptions.None).First();
            EnumTableName = token.Split("::").Last();
         } else if (token.Contains(":")) {
            (Type, Length) = (ArgType.Short, 2);
            Name = token.Split(':').First();
            EnumTableName = token.Split(':').Last();
         } else {
            // didn't find a token :(
            // I guess it's a byte?
            (Type, Length) = (ArgType.Byte, 1);
            Name = token;
         }
      }

      public string Convert(IDataModel model, int value) {
         var byteText = value.ToString($"X{Length * 2}");
         if (string.IsNullOrEmpty(EnumTableName)) return byteText;
         var table = model.GetOptions(EnumTableName);
         if ((table?.Count ?? 0) <= value) return byteText;
         return table[value];
      }

      public int Convert(IDataModel model, string value) {
         int result;
         if (string.IsNullOrEmpty(EnumTableName)) {
            if (int.TryParse(value, NumberStyles.HexNumber, CultureInfo.CurrentCulture, out result)) return result;
            return 0;
         }
         if (ArrayRunEnumSegment.TryParse(EnumTableName, model, value, out result)) return result;
         return 0;
      }
   }

   public enum ArgType {
      Byte,
      Short,
      Word,
      Pointer,
   }

   public static class ScriptExtensions {
      public static ScriptLine GetMatchingLine(this IReadOnlyList<ScriptLine> self, IReadOnlyList<byte> data, int start) => self.FirstOrDefault(option => option.Matches(data, start));

      public static int GetScriptSegmentLength(this IReadOnlyList<ScriptLine> self, IDataModel model, int address) {
         int length = 0;
         while (true) {
            var line = self.GetMatchingLine(model, address + length);
            if (line == null) break;
            length += line.CompiledByteLength;
            if (line.IsEndingCommand) break;
         }
         return length;
      }
   }
}
