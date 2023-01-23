﻿using HavenSoft.HexManiac.Core;
using HavenSoft.HexManiac.Core.Models;
using HavenSoft.HexManiac.Core.Models.Code;
using HavenSoft.HexManiac.Core.Models.Runs;
using HavenSoft.HexManiac.Core.ViewModels.Tools;
using HavenSoft.HexManiac.Core.ViewModels.Visitors;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace HavenSoft.HexManiac.Tests {
   public class CodeToolTests : BaseViewModelTestClass {
      private CodeTool Tool => ViewPort.Tools.CodeTool;
      private string EventScript {
         get => ";".Join(Tool.Contents[0].Content.SplitLines().Select(line => line.Trim()));
         set {
            Tool.Mode = CodeMode.Script;
            Tool.Contents[0].Content = value.Replace(";", Environment.NewLine);
         }
      }
      private string ThumbScript {
         set => Tool.Content = value.Replace(";", Environment.NewLine);
      }
      private string GetContent(int index) => Tool.Contents[index].Content;
      private void SetContent(int index, string content) => Tool.Contents[index].Content = content.Replace(";", Environment.NewLine);

      private void AddScriptFormat(int index) {
         ViewPort.Goto.Execute(index);
         var point = ViewPort.ConvertAddressToViewPoint(index);
         var contextItems = new ContextItemFactory(ViewPort);
         ViewPort[point].Format.Visit(contextItems, 0xFF);
         var group = (ContextItemGroup)contextItems.Results.Single(result => result.Text.StartsWith("Create New"));
         group.Single(item => item.Text.StartsWith("Event Script")).Command.Execute();
      }

      public CodeToolTests() => SetFullModel(0xFF);

      [Fact]
      public void AddAndRemoveAnchorInSameToken_Undo_NoAnchor() {
         WriteEventScript(0x10, "end");
         WriteEventScript(0x20, "goto <010>");

         EventScript = "goto <00000>";
         EventScript = "goto <000030>";
         EventScript = "goto <00000>";
         EventScript = "goto <000010>";

         ViewPort.Undo.Execute();

         var run = Model.GetNextRun(0x30);
         Assert.NotEqual(0x30, run.Start);
      }

      [Fact]
      public void ScriptWithCallAndLabel_Expand_NoError() {
         Tool.Mode = CodeMode.Script;
         EventScript = "call <routine>;end;routine:;end";

         // since the routine is part of the script, it doesn't need to be a separate content box
         Assert.Single(Tool.Contents);

         EventScript = "call <routine>;nop;end;routine:;end";

         Assert.False(Tool.ShowErrorText);
      }

      [Fact]
      public void ScriptWithTwoEnds_ChangeEndToNop_NoErrors() {
         Tool.Mode = CodeMode.Script;

         EventScript = "nop;end;end";
         EventScript = "nop;nop;end";

         Assert.False(Tool.ShowErrorText);
      }

      [Fact]
      public void FireRedSpecial_Decode_HasLabel() {
         SetGameCode("BPRE0");
         foreach (var meta in BaseModel.GetDefaultMetadatas("bpre")) Model.LoadMetadata(meta);
         Token.ChangeData(Model, 0, "25 9E 00 02".ToByteArray());

         var script = Tool.ScriptParser.Parse(Model, 0, 4).SplitLines()[0].Trim();

         Assert.Equal("special ChangePokemonNickname", script);
      }

      [Fact]
      public void ThumbCode_HasComment_Compiles() {
         var result = Tool.Parser.Compile(Model, 0,
            "nop/*",
            "nop",
            "*/nop",
            "nop");
         Assert.Equal(6, result.Count);
      }

      [Fact]
      public void EquDirective_Compile_DoesTextSubstitution() {
         var result = Tool.Parser.Compile(Model, 0,
            ".equ candy, 7",
            "mov  r0, candy");
         Assert.Equal(2, result.Count);
         Assert.Equal(0b00100_000_00000111, result.ReadMultiByteValue(0, 2));
      }

      [Fact]
      public void ByteDirective_Compile_Supported() {
         var result = Tool.Parser.Compile(Model, 0, ".byte 10", ".byte 0x10");
         Assert.Equal(new byte[] { 10, 0x10 }, result);
      }

      [Fact]
      public void ByteDirective_ThenCode_AlignmentAdded() {
         var result = Tool.Parser.Compile(Model, 0, ".byte 10", "nop");
         Assert.Equal(new byte[] { 10, 0, 0, 0 }, result);
      }

      [Fact]
      public void HalfWordDirective_Compile_Supported() {
         var result = Tool.Parser.Compile(Model, 0, ".hword 10", ".hword 0x10");
         Assert.Equal(new byte[] { 10, 0, 0x10, 0 }, result);
      }

      [Fact]
      public void CommandWithPointerToValidData_ChangePointerToUnknown_CreateNewCopyOfExistingData() {
         Tool.Mode = CodeMode.Script;
         EventScript = "loadpointer 0 <40>;{;test;};end";

         EventScript = "loadpointer 0 <??????>;{;test;};end";

         var run = (PCSRun)Model.GetNextRun(Model.ReadPointer(2));
         Assert.NotEqual(0x40, run.Start);
         Assert.Equal("test", run.SerializeRun());
         Assert.Equal(2, EventScript.Count("{}".Contains));
      }

      [Fact]
      public void OutOfOrderJumps_EditBottomScript_ScriptCountRemainsSame() {
         Tool.Mode = CodeMode.Script;
         AddScriptFormat(0);
         EventScript = "if2 = <028>; if2 = <018>; if2 = <020>; end";
         ViewPort.Edit("@018 02 @020 02 @028 02 @000 ");
         ViewPort.ExpandSelection(0, 0);

         SetContent(2, GetContent(2) + " ");

         Assert.Equal(4, Tool.Contents.Count);
      }

      [Fact]
      public void ScriptWithInnerAnchor_AddScriptFormat_AnchorKept() {
         Tool.Mode = CodeMode.Script;
         EventScript = "lock;faceplayer;end";
         ViewPort.Edit("@010 <001>");

         ViewPort.Goto.Execute(0);
         ViewPort.Edit("^`xse`");

         Assert.IsType<PointerRun>(Model.GetNextRun(0x10));
      }

      [Fact]
      public void ScriptWithInnerAnchor_AddPointerAtThatLocation_AnchorRemoved() {
         Tool.Mode = CodeMode.Script;
         EventScript = "lock;if1 = <100>;end";
         ViewPort.Edit("@010 <005>"); // points into the pointer

         ViewPort.Goto.Execute(0);
         ViewPort.Edit("^`xse`");

         Assert.IsType<PointerRun>(Model.GetNextRun(5));
         Model.ResolveConflicts();
      }

      [Fact]
      public void AddWithTwoSameRegisters_AddWithOneRegister_SameCode() {
         ThumbScript = "add r3,r3,#6; add r3, #6";
         Assert.Equal(Model.ReadMultiByteValue(0, 2), Model.ReadMultiByteValue(2, 2));
      }

      [Fact]
      public void Word_PlusOne_Compiles() {
         ThumbScript = ".word 0x8123456+1";
         Assert.Equal(0x57, Model[0]);
      }

      [Fact]
      public void TrainerScript_Decompile_MultipleFormatSections() {
         // 5C trainerbattle 00 trainer:data.trainers.stats arg: start<""> playerwin<"">
         SetFullModel(0xFF);
         ViewPort.Edit("5C 00 0000 0000 <100> <110> 02 @100 ^start\"\"Start\" @110 ^win\"\"Win\"");
         ViewPort.CascadeScript(0);
         var code = ViewPort.Tools.CodeTool.ScriptParser.Parse(Model, 0, 15).SplitLines().Select(line=>line.Trim()).ToArray();
         var expected = new[] {
            "trainerbattle 00 0 0 <000100> <000110>",
            "{",
            "Start",
            "}",
            "{",
            "Win",
            "}",
            "end",
            "",
         };
         Assert.All(code.Length.Range(), i => Assert.Equal(expected[i], code[i]));
      }

      [Fact]
      public void TrainerScript_MultipleFormatSections_Compiles() {
         SetFullModel(0xFF);
         Tool.Mode = CodeMode.Script;

         EventScript = "trainerbattle 0 0 0 <100> <110>;{;Start;};{;Win;};end";

         var results = new byte[16];
         Array.Copy(Model.RawData, 0, results, 0, 16);
         Assert.Equal(new byte[] { 0x5C, 0, 0,0, 0,0, 0,1,0,8, 0x10,1,0,8, 2, 0xFF }, results);
         Assert.Equal("\"Start\"", Model.TextConverter.Convert(Model, 0x100, 0x20));
         Assert.Equal("\"Win\"", Model.TextConverter.Convert(Model, 0x110, 0x20));
      }

      [Theory]
      [InlineData("npc", 2)]
      [InlineData("sign", 3)]
      [InlineData("default", 4)]
      [InlineData("yesno", 5)]
      [InlineData("autoclose", 6)]
      public void Macro_Compile_Compiles(string type, byte value) {
         SetFullModel(0xFF);
         Tool.Mode = CodeMode.Script;

         EventScript = $"msgbox.{type} <100>;end";

         var expected = $"0F 00 00 01 00 08 09 {value:X2} 02".ToByteArray();
         Assert.All(expected.Length.Range(), i => Assert.Equal(expected[i], Model[i]));
      }

      [Theory]
      [InlineData("npc", 2)]
      [InlineData("sign", 3)]
      [InlineData("default", 4)]
      [InlineData("yesno", 5)]
      [InlineData("autoclose", 6)]
      public void Macro_Decompile_Decompiles(string type, byte value) {
         SetFullModel(0xFF);
         $"0F 00 00 01 00 08 09 {value:X2} 02".ToByteArray().WriteInto(Model.RawData, 0);
         var lines = Tool.ScriptParser.Parse(Model, 0, 9).SplitLines();
         Assert.Equal($"msgbox.{type} <000100>", lines[0].Trim());
      }

      [Fact]
      public void BranchlinkOutOfReach_ConvertToLongBranchLink() {
         ThumbScript = "push {lr}; bl <C00000>; pop {pc}";

         // we want to branch to C00000
         // but that's beyond the maximum branch distance!
         // install a 'universal-branch-link' command to allow it.

         // the universal-branch-link is a normal branch-link,
         // but instead of going where we want to go, it goes
         // to a bit of long-jump code first.
         // (1) push r0 for extra space, and push r1 so we can use the register.
         // (2) fill r1 with the destination we want to branch to (+1)
         // (3) store the value from r1 to the stack
         // (4) pop to restore the value of r1, and also jump to the address on the stack

         var expected = new byte[] {
            00, 0b10110101,        // push {lr}
            0x00, 0b11110_000, 0x01, 0b11111_000, // bl round4(pc+4+2*1)
            0x00, 0xBD,            // pop {pc}
            // the magic part
            0b11, 0xB4,            // push {r0-r1}
            0x01, 0b01001_001,     // ldr r1, pc+4+4* 1
            0x01, 0b10010_001,     // str r1, sp+4*   1
            0b10, 0xBD,            // pop {pc, r1}
            0x01, 0x00, 0xC0, 0x08 // .word 0xC00000
         };

         var result = new byte[expected.Length];
         Array.Copy(Model.RawData, 0, result, 0, result.Length);
         Assert.All(result.Length.Range(), i => Assert.Equal(expected[i], result[i]));
      }

      [Fact]
      public void BranchLinksOutOfReach_TwoDistinctAddresses_TwoLongBranchLinks() {
         ThumbScript = "push {lr}; bl <C00000>; bl <D00000>; pop {pc}";

         var expectedLength = 12 + 12 * 2; // 12 bytes for the initial command, 12 bytes for each long-branch-link
         Assert.NotEqual(0xFF, Model.RawData[expectedLength - 1]);
         Assert.Equal(0xFF, Model.RawData[expectedLength]);
      }

      [Fact]
      public void BranchLinksOutOfReach_TwoIdenticalAddresses_OneLongBranchLink() {
         ThumbScript = "push {lr}; bl <C00000>; bl <C00000>; pop {pc}";

         var expectedLength = 12 + 12 * 1; // 12 bytes for the initial command, 12 bytes for a single long-branch-link
         Assert.NotEqual(0xFF, Model.RawData[expectedLength - 1]);
         Assert.Equal(0xFF, Model.RawData[expectedLength]);
      }

      [Fact]
      public void ExactlyOneThumbRoutine_Cut_UniversalBranchLinkLeftBehind() {
         ThumbScript = "push {lr}; nop; nop; nop; nop; nop; nop; nop; nop; pop {pc}; push {lr}";

         ViewPort.SelectionStart = new Point(0, 0); // 2nd byte of selection is B5
         ViewPort.SelectionEnd = ViewPort.ConvertAddressToViewPoint(0x13); // 2nd byte after selection is B5
         ViewPort.Cut(FileSystem);

         var text = FileSystem.CopyText.value;
         var anchor = text.Split(' ')[0].Substring(1); // something like ^thumb.misc.000000
         var expected = "03 B4 01 49 01 91 02 BD".ToByteArray();
         Assert.Contains(".thumb", text);
         Assert.Contains(".end", text);
         Assert.All(expected.Length.Range(), i => Assert.Equal(expected[i], Model[i]));
         var run = (OffsetPointerRun)Model.GetNextRun(0x8);
         Assert.Equal(1, run.Offset);
         Assert.Equal(0x8, Model.GetUnmappedSourcesToAnchor(anchor).Single());
      }

      [Theory]
      [InlineData(0)]
      [InlineData(2)]
      [InlineData(4)]
      [InlineData(9 * 2)]
      [InlineData(10 * 2)]
      public void NotExactSelection_Cut_DataCleared(int start) {
         ThumbScript = "nop; push {lr}; nop; nop; nop; nop; nop; nop; nop; nop; pop {pc}; push {lr}";
         ViewPort.SelectionStart = ViewPort.ConvertAddressToViewPoint(start);
         ViewPort.SelectionEnd = ViewPort.ConvertAddressToViewPoint(start + 3);

         ViewPort.Cut(FileSystem);

         var expected = "FF FF FF FF".ToByteArray();
         Assert.All(expected.Length.Range(), i => Assert.Equal(expected[i], Model[start + i]));
      }

      [Fact]
      public void SelectStartOfThumbRoutine_ExpandSelection_SelectThumbRoutine() {
         ThumbScript = "push {lr}; nop; nop; nop; nop; nop; nop; nop; nop; pop {pc}; push {lr}";

         ViewPort.SelectionStart = new Point(0, 0);
         ViewPort.ExpandSelection(0, 0);

         Assert.Equal(19, ViewPort.ConvertViewPointToAddress(ViewPort.SelectionEnd));
      }

      [Fact]
      public void LongBranchLink_OddNumberOfCommands_NopInsertedBeforeLongBranchLinkCode() {
         ThumbScript = "bl <C00000>; bx r0";
         Assert.Equal(0, Model.ReadMultiByteValue(6, 2));
      }

      [Fact]
      public void LongBranchLinkOddNumberCommands_StartsAtMultipleOfTwo_NoNopInserted() {
         ViewPort.Goto.Execute(2);
         ThumbScript = "bl <C00000>; bx r0";
         Assert.NotEqual(0, Model.ReadMultiByteValue(8, 2));
      }

      [Fact]
      public void CodeWithPointer_DoubleClick_SelectCodeAndPointer() {
         ThumbScript = "push {lr}; ldr r0, =<800000>; pop {pc}";
         ViewPort.SelectionStart = ViewPort.ConvertAddressToViewPoint(12);
         ThumbScript = "push {lr}";

         ViewPort.Goto.Execute(0);
         ViewPort.ExpandSelection(0, 0);

         Assert.IsType<PointerRun>(Model.GetNextRun(8));
         Assert.Equal(11, ViewPort.ConvertViewPointToAddress(ViewPort.SelectionEnd));
      }

      [Fact]
      public void CodeWithPointer_ReplaceWithLongBranchLink_Success() {
         ThumbScript = "push {lr}; ldr r0, =<800000>; pop {pc}";
         ViewPort.Goto.Execute(12);
         ThumbScript = "push {lr}";

         ViewPort.Goto.Execute(0);
         ViewPort.ExpandSelection(0, 0);
         ViewPort.Cut(FileSystem);

         // if we succeeded, then the value at 0x8 should be <null>
         Assert.Equal(0, Model.ReadMultiByteValue(8, 4));
      }

      [Fact]
      public void AnchorAtStart_ExpandSelectionToThumbRoutine_SelectionExpanded() {
         ThumbScript = "push {lr}; pop {pc}; push {lr}";
         ViewPort.Edit("^some.anchor ");

         ViewPort.ExpandSelection(0, 0);

         Assert.Equal(3, ViewPort.ConvertViewPointToAddress(ViewPort.SelectionEnd));
      }

      [Fact]
      public void ThumbRoutineWithPointer_Cut_RemovePointerFormat() {
         ThumbScript = "push {lr}; nop; nop; nop; nop; nop; nop; nop; nop; nop; nop; ldr r0, =<800000>; pop {pc}";

         ViewPort.ExpandSelection(0, 0);
         ViewPort.Cut(FileSystem);

         Assert.IsType<OffsetPointerRun>(Model.GetNextRun(8));
         Assert.IsNotType<PointerRun>(Model.GetNextRun(0xC));
      }

      [Fact]
      public void AutoPointer_CreateText_DirectlyAfterScript() {
         Tool.Mode = CodeMode.Script;

         EventScript = "loadpointer 0 <auto>;{;text;};end";

         Assert.IsType<PCSRun>(Model.GetNextRun(7));
      }

      [Fact]
      public void Text_DirectlyAfterScript_InterpretAsAutoPointer() {
         ViewPort.Edit("0F 00 <007> 02 ^\"\" text\" @000 ^script.start`xse`");

         var script = EventScript;

         Assert.Contains("<auto>", script);
      }

      [Fact]
      public void ScriptIfElseShape_Decompile_OneTextBoxWithFullScript() {
         EventScript = @"
if1 = <part2>
  nop
  goto <part3>
part2:
  nop
part3:
  nop
  end
";

         var length = Tool.ScriptParser.GetScriptSegmentLength(Model, 0);
         var content = Tool.ScriptParser.Parse(Model, 0, length);

         Assert.Equal(15, length);
         Assert.Single(Tool.Contents);
         Assert.Contains("end", content);
      }

      [Fact]
      public void Mart_Auto_Compiles() {
         EventScript = @"
pokemart <auto>
{
1
2
3
}
  end
";

         var run = Model.GetNextRun(6);
         Assert.IsAssignableFrom<ITableRun>(run);
         Assert.Equal(1, Model.ReadMultiByteValue(6, 2));
         Assert.Equal(2, Model.ReadMultiByteValue(8, 2));
         Assert.Equal(3, Model.ReadMultiByteValue(10, 2));
         Assert.Equal(0, Model.ReadMultiByteValue(12, 2));
      }

      [Fact]
      public void Mart_Decompile_Auto() {
         ViewPort.Edit($"^some.script`xse` 86 <006> 02 01 00 02 00 03 00 00 00 @006 ^[item:{HardcodeTablesModel.ItemsTableName}]!0000");

         ViewPort.Goto.Execute(0);
         var script = EventScript;

         Assert.Contains("<auto>", script);
      }

      [Fact]
      public void Movement_Auto_Compiles() {
         EventScript = @"
applymovement 0 <auto>
{
1
2
3
}
  end
";

         var run = Model.GetNextRun(9);
         Assert.IsAssignableFrom<ITableRun>(run);
         Assert.Equal(1, Model[8]);
         Assert.Equal(2, Model[9]);
         Assert.Equal(3, Model[10]);
         Assert.Equal(0xFE, Model[11]);
      }

      [Fact]
      public void Movement_Decompile_Auto() {
         ViewPort.Edit($"^some.script`xse` 4F 00 00 <008> 02 01 02 03 FE @008 ^[move.movementtypes]!FE");

         ViewPort.Goto.Execute(0);
         var script = EventScript;

         Assert.Contains("<auto>", script);
      }

      [Fact]
      public void ScriptWithTextAfterBranch_Decompile_IncludedInLength() {
         EventScript = @"
  lock
  msgbox.yesno <auto>
{
Is the answer yes?
}
  if.compare 0x800D = 1 <yes>
  if.compare 0x800D = 0 <no>
  release
  end
yes:
  msgbox.default <auto>
{
You said yes!
}
  release
  end
no:
  msgbox.default <auto>
{
You said no!
}
  release
  end";

         var length = Tool.ScriptParser.FindLength(Model, 0);

         Assert.Equal(99, length);
      }

      [Fact]
      public void ScriptBranchesToAnotherScriptDirectlyAfter_Decompile_OneContentBox() {
         ViewPort.Edit("06 00 <007> 02 00 02 @007 ^`xse` ");

         Tool.Mode = CodeMode.Script;
         ViewPort.Goto.Execute(0);

         Assert.Single(Tool.Contents);
      }

      [Fact]
      public void Macro_GameSpecific_Compiles() {
         var line = new MacroScriptLine("[BPRE_BPGE] some.command 01 varible: 33 # comment");
         Assert.True(line.MatchesGame("BPRE"));
         Assert.False(line.MatchesGame("BPEE"));
      }

      [Fact]
      public void PointerToUnformattedAnchor_ScriptExpected_NoAuto() {
         ViewPort.Edit("05 <005> @000 ");
         Tool.Mode = CodeMode.Script;
         Assert.DoesNotContain("<auto>", EventScript);
      }

      [Fact]
      public void BranchToScriptAfterCurrentAddress_SecondScriptHasGoto_DestinationInNewScriptContent() {
         Tool.Mode = CodeMode.Script;
         EventScript = "if1 = <later>;end;later:;goto <000100>";
         ViewPort.Goto.Execute(0x100);
         EventScript = "nop;end";

         ViewPort.Goto.Execute(0);

         Assert.Equal(2, Tool.Contents.Count);
      }

      [Fact]
      public void AnimationScript_PlaySeWithPan_CanDecode() {
         var animationpan = new List<string>(192.Range<string>(i => null));
         animationpan[63] = "sound_pan_target";
         ViewPort.Model.SetList(nameof(animationpan), animationpan);

         ViewPort.Edit("19 00 00 00 ");
         Tool.Mode = CodeMode.AnimationScript;
         ViewPort.Goto.Execute(0);
         var help = Tool.AnimationScriptParser.GetHelp(ViewPort.Model, new HelpContext("playsewithpan mus_dummy 0", 25));

         Assert.NotNull(help);
      }

      [Fact]
      public void Script_UnusedLabel_LabelAppendedToEndOfScript() {
         EventScript = "if1 = <go1>;if1 = <go2>;end"; // the end is at 12

         Assert.Equal(13, Model.ReadPointer(2));
         Assert.Equal(14, Model.ReadPointer(8));
      }

      [Fact]
      public void Script_EndsInLabel_AutoIncludeEndCommand() {
         EventScript = "if1 = <go1>;end;go1:";

         Assert.Equal(7, Model.ReadPointer(2));
         Assert.Equal(2, Model[7]);
      }

      [Fact]
      public void Script_Unfinished_EndsWithEndCommand() {
         EventScript = "nop";
         Assert.Equal(2, Model[1]);
      }

      [Fact]
      public void Script_EndsWithGoto_DoesNotIncludeClosingEnd() {
         EventScript = "goto <100>";
         Assert.Equal(0xFF, Model[5]);
      }

      // TODO test that we get an error (not an exception) if we do auto on an unformatted pointer
   }
}
