﻿using System;
using System.Collections.Generic;
using System.Text;
using Romulus;
using System.IO;
using System.Diagnostics;

namespace snarfblasm
{
    class Parser
    {

        private readonly Assembler Assembler;
        private AssemblyData assembly { get; set; }

        public Parser(Assembler assembler) {
            this.Assembler = assembler;
        }

        NamespacedLabelName mostRecentNamedLabel = new NamespacedLabelName("_nolabel_", null);


        // Todo: Move TryToConvertToZeroPage and FindOpcode to another class, probably assembler. 
        // (The only reason they need to be instance methods is for AllowInvalidOpcodes, but this
        //  could also be specified as a parameter).
        public bool TryToConvertToZeroPage(ref ParsedInstruction instruction) {
            var op = Opcode.allOps[instruction.opcode];
            var addressing = op.addressing;
            Opcode.addressing newAddressing = addressing;

            switch (addressing) {
                case Opcode.addressing.absolute:
                    newAddressing = Opcode.addressing.zeropage;
                    break;
                case Opcode.addressing.absoluteIndexedX:
                    newAddressing = Opcode.addressing.zeropageIndexedX;
                    break;
                case Opcode.addressing.absoluteIndexedY:
                    newAddressing = Opcode.addressing.zeropageIndexedY;
                    break;
            }

            if (addressing == newAddressing) return false;

            int newOpcode = FindOpcode(op.name, newAddressing);
            if (newOpcode >= 0) {
                instruction = new ParsedInstruction(instruction, (byte)newOpcode);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Parses code from a list of sub-strings. (Useful if the code
        /// is in the form of a single long string.)
        /// </summary>
        /// <param name="lines"></param>
        public void ParseCode(IList<StringSection> lines) {
            assembly = Assembler.Assembly;

            Error error;
            for (int i = 0; i < lines.Count; i++) {
                ParseLine(lines[i], i, out error);

                if (error.Code != ErrorCode.None) {
                    Assembler.AddError(error);
                }
            }

        }




        /// <summary>
        /// 
        /// </summary>
        /// <param name="line">The line of code to parse.</param>
        /// <param name="sourceLine">The line number of the code file.</param>
        void ParseLine(StringSection line, int iSourceLine, out Error error) {

            error = Error.None;

            line = line.TrimLeft();
            RemoveComments(ref line);

            int newInstructionIndex = assembly.ParsedInstructions.Count;

            bool loopLabel;
            do { // This loop allows us to parse alternating named and anonymous labels (* label: +++ -- anotherLabel:)
                loopLabel = false
                    | ParseNamedLabels(ref line, newInstructionIndex, iSourceLine)
                    | ParseAnonymousLabel(ref line, newInstructionIndex, iSourceLine);
            } while (loopLabel);

            if (line.IsNullOrEmpty || line[0] == ';') {
                // Nothing on this line
                return;
            }

            // Dot-prefixed directive
            if (line[0] == '.') {
                line = line.Substring(1);
                var directiveName = GrabSimpleName(ref line).ToString();
                line = line.TrimLeft();

                if (!ParseDirective(directiveName, line, iSourceLine, out error)) {
                    error = new Error(ErrorCode.Directive_Not_Defined, string.Format(Error.Msg_DirectiveUndefined_name, directiveName), iSourceLine);
                    return;
                }
                return;
            }



            bool parsedUncolonedLabel = false; // Set to true when an uncoloned label is found so we don't look for one again.
            bool loopAgain = false; // Set to true when an uncoloned label is found to loop back and try to parse the remaining text
            do {
                loopAgain = false;

                var symbol = GrabLabelName(ref line, true);
                //var symbol = GrabSimpleName(ref line);
                if (symbol.IsEmpty) break;
                line = line.TrimLeft();

                if (ParseDirective(symbol, line, iSourceLine, out error))
                    return;
                else if (symbol.IsSimple && ParseInstruction(symbol.name, line, iSourceLine, out error)) {
                    return;
                } else if ((line.Length > 0 && line[0] == '=') || (line.Length > 1 && line[0] == ':' && line[1] == '=')) { // Assignments and label assignments
                    // := is a cross between a label and assignment: It declares a label with an explicit value.
                    bool isLabel = (line[0] == ':');

                    var expression = line.Substring(1).TrimLeft();
                    if (isLabel) expression = expression.Substring(1).TrimLeft();

                    if (expression.IsNullOrEmpty) {
                        error = new Error(ErrorCode.Expected_Expression, Error.Msg_ExpectedValue, iSourceLine);
                    } else {
                        ParseAssignment(symbol, isLabel, expression, iSourceLine);
                    }
                } else {
                    // Todo: ensure 'symbol' is a valid label name
                    //var labelName = GrabLabelName(ref symbol, false);
                    var symbolName = symbol.name ?? "";

                    if (!Assembler.RequireColonOnLabels && !parsedUncolonedLabel && symbolName.Length > 0) {
                        if (symbol.IsSimple && symbolName[0] == '@') {
                            var fullName = new NamespacedLabelName(mostRecentNamedLabel.name + "." + symbol.name, mostRecentNamedLabel.nspace);
                            assembly.Labels.Add(new NamespacedLabel(fullName, newInstructionIndex, iSourceLine, true));
                        } else {
                            // Todo: something feels off here. Shouldn't the label be placed in the current namespace? But that's not evaluated until the actual assembly step...
                            mostRecentNamedLabel = symbol;// symbol.ToString();
                            //var nspace = string.IsNullOrEmpty(symbol.nspace) ? null : symbol.nspace;
                            assembly.Labels.Add(new NamespacedLabel(symbol, newInstructionIndex, iSourceLine, false));
                        }
                        parsedUncolonedLabel = true;
                        loopAgain = true;

                    } else {
                        error = new Error(ErrorCode.Unexpected_Text, Error.Msg_BadLine, iSourceLine);
                    }
                }
            } while (parsedUncolonedLabel && loopAgain);
        }



        private int NextInstructionIndex { get { return assembly.ParsedInstructions.Count; } }
        private void ParseAssignment(NamespacedLabelName symbolName, bool isLabel, StringSection expression, int iSourceLine) {
            // Call site SHOULD check for this condition and specify an Error.
            if (expression.Length == 0) throw new SyntaxErrorException("Expected: expression.", iSourceLine);

            AsmValue assignedValue;

            LiteralValue assignedLiteral;
            if (ExpressionEvaluator.TryParseLiteral(expression, out assignedLiteral)) {
                assignedValue = new AsmValue(assignedLiteral);
            } else {
                assignedValue = new AsmValue(expression.ToString());
            }

            assembly.Directives.Add(new Assignment(NextInstructionIndex, iSourceLine, symbolName, isLabel, assignedValue));

        }

        private bool ParseInstruction(StringSection instruction, StringSection operand, int iSourceLine, out Error error) {
            error = Error.None;
            if (operand.Length > 0 && operand[0] == '@') { }
            var addressing = ParseAddressing(ref operand);
            int opcodeVal = FindOpcode(instruction, addressing);

            // Some instructions only support zero-page addressing variants of certain addressing modes
            if ((OpcodeError)opcodeVal == OpcodeError.InvalidAddressing) {
                var newAddressing = addressing;
                if (TryZeroPageEqivalent(ref newAddressing)) {
                    opcodeVal = FindOpcode(instruction, newAddressing);
                    if (opcodeVal >= 0) addressing = newAddressing; // Keep the zero-page addressing 
                }
            }

            if (opcodeVal < 0) {
                // We don't throw an error if 'instruction' isn't an instruction (the line could be anything else other than instruction)
                if ((OpcodeError)opcodeVal == OpcodeError.UnknownInstruction) {
                    return false;
                } else {
                    SetOpcodeError(iSourceLine, instruction.ToString(), addressing, opcodeVal, out error);
                    return true;
                }
            }

            if (addressing != Opcode.addressing.implied) {
                LiteralValue operandValue = new LiteralValue();

                // Todo: consider method(s) such as assembly.AddInstruction
                if (ExpressionEvaluator.TryParseLiteral(operand, out operandValue)) {
                    assembly.ParsedInstructions.Add(new ParsedInstruction((byte)opcodeVal, operandValue, iSourceLine));
                } else if (operand.Length > 0) {
                    assembly.ParsedInstructions.Add(new ParsedInstruction((byte)opcodeVal, operand.Trim().ToString(), iSourceLine));
                } else { // no operand
                    assembly.ParsedInstructions.Add(new ParsedInstruction((byte)opcodeVal, default(LiteralValue), iSourceLine));
                }
            } else {
                assembly.ParsedInstructions.Add(new ParsedInstruction((byte)opcodeVal, default(LiteralValue), iSourceLine));
            }

            return true;
        }

        private bool TryZeroPageEqivalent(ref Opcode.addressing addressing) {
            switch (addressing) {
                case Opcode.addressing.absolute:
                    addressing = Opcode.addressing.zeropage;
                    return true;
                case Opcode.addressing.absoluteIndexedX:
                    addressing = Opcode.addressing.zeropageIndexedX;
                    return true;
                case Opcode.addressing.absoluteIndexedY:
                    addressing = Opcode.addressing.zeropageIndexedY;
                    return true;
                default:
                    return false;
            }
        }

        private void RemoveComments(ref StringSection line) {
            // ; denotes a comment, except within a string
            bool inString = false;

            for (int i = 0; i < line.Length; i++) {
                if (inString) {
                    if (line[i] == '\"') { // End of string
                        // unless it is preceeded by a backslash (then it's as escaped quote)
                        if (i == 0 || line[i - 1] != '\\')
                            inString = false;
                    }
                } else {
                    if (line[i] == ';') { // Comment
                        line = line.Substring(0, i);
                        return;
                    } else if (line[i] == '\"') { // Start of string
                        inString = true;
                    }

                }
            }
        }


        static List<char> charBuilder = new List<char>();
        static object ParseLock = new object();

        static char[] escapeCodes = new char[] { 't', 'r', 'n', '\"' };
        static char[] escapeValues = new char[] { '\t', '\r', '\n', '\"' };
        /// <summary>
        /// Returns a char array containing all the characters from a string. Escapes are processed. The specified string
        /// should not include the opening quote. The parsed string will be removed from the string passed in. The closing
        /// quote will not be removed, so that the caller can examine it and verify that there was a closing quote.
        /// </summary>
        /// <param name="section"></param>
        /// <returns></returns>
        public static char[] ParseString(ref StringSection str, out ErrorCode error) {
            error = ErrorCode.None;

            // Only one thread can run this method at a time
            lock (ParseLock) {
                charBuilder.Clear();

                while (str.Length > 0) {
                    char c = str[0];
                    if (c == '\"') {
                        return charBuilder.ToArray();
                    } else if (c == '\\') {
                        str = str.Substring(1);
                        if (str.Length > 0) {
                            c = str[0];

                            int escapeIndex = Array.IndexOf(escapeCodes, c);
                            if (escapeIndex < 0) {
                                error = ErrorCode.Invalid_Escape;
                                return null;
                            } else {
                                charBuilder.Add(escapeValues[escapeIndex]);
                            }
                        } else {
                            error = ErrorCode.Invalid_Escape;
                            return null;
                        }
                    } else {
                        charBuilder.Add(c);
                    }
                    str = str.Substring(1);
                }

                return charBuilder.ToArray();
            }
        }
        /// <summary>
        /// Returns a symbol name (identifier or '$' character), or a zero-length string if no symbol was found.
        /// </summary>
        /// <param name="exp"></param>
        /// <returns></returns>
        /// <remarks>This function will parse "namespace::identifier" as a single symbol. </remarks>
        private NamespacedLabelName GetSymbol(StringSection exp) {
            //// Check for special '$' variable
            //if (exp.Length > 0 && exp[0] == '$' && !char.IsLetter(exp[0]) && !char.IsDigit(exp[0])) {
            //    // Todo: how can the char be a letter or digit if its '$'?
            //    return NamespacedLabelName.CurrentInstruction; //"$";
            //}

            //if (exp.Length == 0) return NamespacedLabelName.Empty;
            //if (!char.IsLetter(exp[0]) && exp[0] != '@' && exp[0] != '_') return NamespacedLabelName.Empty;

            //int i = 1;
            //while (i < exp.Length) {
            //    char c = exp[i];
            //    if (char.IsLetter(c) | char.IsDigit(c) | c == '_') {
            //        i++;
            //    } else if(c == ':' && (i + 1 <exp.Length) && exp[i+1] == ':' ) {
            //        // :: <- namespace char
            //        i += 2;
            //    } else {
            //        var result = exp.Substring(0, i);
            //        return result;
            //    }
            //}
            //return exp;
            return GrabLabelName(ref exp, false);
        }

        private StringSection GrabSimpleName(ref StringSection exp) {
            // Check for special '$' variable
            if (exp.Length > 0 && exp[0] == '$') {
                exp = exp.Substring(1);
                return "$";
            }

            if (exp.Length == 0) return StringSection.Empty;
            if (!char.IsLetter(exp[0]) && exp[0] != '@' && exp[0] != '_') return StringSection.Empty;

            int i = 1;
            while (i < exp.Length) {
                char c = exp[i];
                if (char.IsLetter(c) | char.IsDigit(c) | c == '_') {
                    i++;
                } else {
                    // Return up to i
                    var result = exp.Substring(0, i);
                    exp = exp.Substring(i);
                    return result;
                }
            }

            // Return whole thing
            var temp = exp;
            exp = StringSection.Empty;
            return temp;
        }

        private bool ParseDirective(NamespacedLabelName directiveName, StringSection line, int sourceLine, out Error error) {
            if (!directiveName.IsSimple) {
                error = new Error(ErrorCode.Directive_Not_Defined, string.Format(Error.Msg_DirectiveUndefined_name, directiveName.ToString()), sourceLine);
                return false;
            } else {
                return ParseDirective(directiveName.name, line, sourceLine, out error);
            }
        }
        /// <summary>
        /// Returns true of a directive was parsed, even if it was not parsed successfully due to an error
        /// </summary>
        /// <param name="directiveName"></param>
        /// <param name="line"></param>
        /// <param name="sourceLine"></param>
        /// <param name="error"></param>
        /// <returns></returns>
        private bool ParseDirective(string directiveName, StringSection line, int sourceLine, out Error error) {
            error = Error.None;

            if (StringEquals(directiveName, "org", true)) {
                assembly.Directives.Add(new OrgDirective(NextInstructionIndex, sourceLine, new AsmValue(line.ToString())));
            } else if (StringEquals(directiveName, "base", true)) {
                assembly.Directives.Add(new BaseDirective(NextInstructionIndex, sourceLine, new AsmValue(line.ToString())));
            } else if (StringEquals(directiveName, "incbin", true)) {
                assembly.Directives.Add(new IncBinDirective(NextInstructionIndex, sourceLine, line));
            } else if (StringEquals(directiveName, "error", true)) {
                assembly.Directives.Add(new ErrorDirective(NextInstructionIndex, sourceLine, line));
            } else if (StringEquals(directiveName, "patch", true)) {
                assembly.Directives.Add(new PatchDirective(NextInstructionIndex, sourceLine, line.ToString()));
            } else if (StringEquals(directiveName, "define", true)) {
                var remainder = line.Trim();
                if (GrabSimpleName(ref remainder).Length == line.Length) { // line should contain a only a symbol
                    assembly.Directives.Add(new DefineDirective(NextInstructionIndex, sourceLine, line));
                } else {
                    error = new Error(ErrorCode.Expected_LValue, string.Format(Error.Msg_InvalidSymbolName_name, line.ToString()), sourceLine);
                }
            } else if (StringEquals(directiveName, "hex", true)) {
                assembly.Directives.Add(new HexDirective(NextInstructionIndex, sourceLine, line));
            } else if (StringEquals(directiveName, "db", true)) {
                assembly.Directives.Add(new DataDirective(NextInstructionIndex, sourceLine, line, DataDirective.DataType.Bytes));
            } else if (StringEquals(directiveName, "byte", true)) {
                assembly.Directives.Add(new DataDirective(NextInstructionIndex, sourceLine, line, DataDirective.DataType.Bytes));
            } else if (StringEquals(directiveName, "dw", true)) {
                assembly.Directives.Add(new DataDirective(NextInstructionIndex, sourceLine, line, DataDirective.DataType.Words));
            } else if (StringEquals(directiveName, "word", true)) {
                assembly.Directives.Add(new DataDirective(NextInstructionIndex, sourceLine, line, DataDirective.DataType.Words));
            } else if (StringEquals(directiveName, "data", true)) {
                assembly.Directives.Add(new DataDirective(NextInstructionIndex, sourceLine, line, DataDirective.DataType.Implicit));
            } else if (StringEquals(directiveName, "dsb", true)) {
                assembly.Directives.Add(new StorageDirective(NextInstructionIndex, sourceLine, line, StorageDirective.DataType.Bytes));
            } else if (StringEquals(directiveName, "dsw", true)) {
                assembly.Directives.Add(new StorageDirective(NextInstructionIndex, sourceLine, line, StorageDirective.DataType.Words));

            } else if (StringEquals(directiveName, "overflow", true)) {
                assembly.Directives.Add(new OptionDirective(NextInstructionIndex, sourceLine, directiveName.ToString(), line.Trim().ToString()));
            } else if (StringEquals(directiveName, "if", true)
                || StringEquals(directiveName, "else", true)
                || StringEquals(directiveName, "ifdef", true)
                || StringEquals(directiveName, "ifndef", true)
                || StringEquals(directiveName, "endif", true)) {

                assembly.Directives.Add(new ConditionalDirective(NextInstructionIndex, sourceLine, directiveName, line.Trim(), out error));
            } else if (StringEquals(directiveName, "ENUM", true)) {
                assembly.Directives.Add(new EnumDirective(NextInstructionIndex, sourceLine, line));
            } else if (StringEquals(directiveName, "ENDE", true) || StringEquals(directiveName, "ENDENUM", true)) {
                if (line.IsNullOrEmpty) {
                    assembly.Directives.Add(new EndEnumDirective(NextInstructionIndex, sourceLine));
                } else {
                    error = new Error(ErrorCode.Unexpected_Text, Error.Msg_NoTextExpected, sourceLine);
                }
            } else if (StringEquals(directiveName, "signed", true)) {
                assembly.Directives.Add(new OptionDirective(NextInstructionIndex, sourceLine, directiveName.ToString(), line.Trim().ToString()));
            } else if (StringEquals(directiveName, "needdot", true)) {
                assembly.Directives.Add(new OptionDirective(NextInstructionIndex, sourceLine, directiveName.ToString(), line.Trim().ToString()));
            } else if (StringEquals(directiveName, "needcolon", true)) {
                assembly.Directives.Add(new OptionDirective(NextInstructionIndex, sourceLine, directiveName.ToString(), line.Trim().ToString()));
            } else if (StringEquals(directiveName, "alias", true)) {
                var varName = GrabLabelName(ref line, false);
                line = line.Trim();

                if (varName.IsEmpty) {
                    error = new Error(ErrorCode.Syntax_Error, Error.Msg_ExpectedText, sourceLine);
                    return true;
                }
                assembly.Directives.Add(new Assignment(NextInstructionIndex, sourceLine, varName, true, new AsmValue(line.ToString())));
            } else {
                return false;
            }

            return true;

        }


        private void ProcessDirective(StringSection directiveName, StringSection directiveText) {
            string directive = directiveName.ToString().ToUpper();
            switch (directive) {
                case "ORG":

                default:
                    break;
            }
        }

        // This list should be sorted alphabetically.
        StringSection[] directiveNames = { "BASE", "ORG", };
        private bool IsDirective(StringSection directiveName) {
            int listStart = 0;
            int listEnd = directiveNames.Length / 2; // Exclusive

            int listCount = listEnd - listStart;
            while (listCount > 1) {
                // first item of second half
                int split = listStart + listCount / 2;

                var splitText = directiveNames[split];
                var compare = StringSection.Compare(directiveName, splitText, true);
                if (compare == 0) return true;
                if (compare > 0) { // directiveName > splitText
                    listStart = split;
                } else {
                    listEnd = split;
                }

                listCount = listEnd - listStart;
            }
            return StringSection.Compare(directiveName, directiveNames[listStart], true) == 0;
        }

        private StringSection ParseDirectiveName(StringSection text) {
            int i = 0;
            while (i < text.Length && Char.IsLetter(text[i])) {
                i++;
            }
            return text.Substring(0, i);
        }

        private static void SetOpcodeError(int sourceLine, string instructionString, Opcode.addressing addressing, int opcodeVal, out Error error) {
            var errorCode = (OpcodeError)opcodeVal;
            switch (errorCode) {
                case OpcodeError.UnknownInstruction:
                    error = new Error(ErrorCode.Invalid_Instruction, Error.Msg_InstructionUndefined, sourceLine);
                    break;
                case OpcodeError.InvalidAddressing:
                    error = new Error(ErrorCode.Invalid_Instruction, Error.Msg_InstructionBadAddressing, sourceLine);
                    break;
                case OpcodeError.InvalidOpcode:
                    error = new Error(ErrorCode.Invalid_Instruction, Error.Msg_OpcodeInvalid, sourceLine);
                    break;
                default:
                    System.Diagnostics.Debug.Fail("Unexpected error.");
                    error = new Error(ErrorCode.Engine_Error, Error.Msg_Engine_InvalidState);
                    break;
            }
        }


        /// <summary>
        /// Parses one anonymous label (both * and +/- types), if found, and removes it from the expression.
        /// </summary>
        /// <param name="line">The text to parse a label from. This text will be modified to remove the label.</param>
        /// <param name="lineNumber">The index of the first instruction that follows the label.</param>
        /// <param name="iSourceLine">The index of the source line the label occurs on.</param>
        /// <returns>True if a label was parsed.</returns>
        private bool ParseAnonymousLabel(ref StringSection line, int iInstruction, int iSourceLine) {
            // Todo: support named +/-/* labels. This means that the anon label collection will need to be able to store names.

            if (line.Length > 0) {
                if (line[0] == '*') {
                    assembly.AnonymousLabels.AddStarLabel(iSourceLine);
                    assembly.TagAnonLabel(iInstruction, iSourceLine);

                    line = line.Substring(1).TrimLeft();
                    // Remove colon if present
                    if (line.Length > 0 && line[0] == ':') line = line.Substring(1).TrimLeft();
                    return true;
                } else if (line[0] == '+' | line[0] == '-') {
                    ParsePlusOrMinusLabel(ref line, line[0], iInstruction, iSourceLine);
                    return true;
                } else if (line[0] == '{') {
                    assembly.AnonymousLabels.AddLeftBraceLabel(iSourceLine);
                    assembly.TagAnonLabel(iInstruction, iSourceLine);
                    line = line.Substring(1).TrimLeft();
                } else if (line[0] == '}') {
                    assembly.AnonymousLabels.AddRightBraceLabel(iSourceLine);
                    assembly.TagAnonLabel(iInstruction, iSourceLine);
                    line = line.Substring(1).TrimLeft();
                }
            }
            return false;
        }



        private void ParsePlusOrMinusLabel(ref StringSection line, char labelChar, int iInstruction, int iSourceLine) {
            int charCount = 1; // Number of times the label char (+ or -) appears
            while (charCount < line.Length && line[charCount] == labelChar)
                charCount++;


            if (labelChar == '+') {
                assembly.AnonymousLabels.AddPlusLabel(charCount, iSourceLine);
            } else if (labelChar == '-') {
                assembly.AnonymousLabels.AddMinusLabel(charCount, iSourceLine);
            } else {
                throw new ArgumentException("Invalid label character for +/- label.", "labelChar");
            }
            assembly.TagAnonLabel(iInstruction, iSourceLine);

            line = line.Substring(charCount).Trim();
            // Remove colon if present
            if (line.Length > 0 && line[0] == ':') line = line.Substring(1).TrimLeft();

        }

        private bool ParseNamedLabels(ref StringSection line, int iParsedLine, int iSourceLine) {
            var lineCopy = line; // Don't want to mutate line if we don't actually do anything
            var labelName = GrabLabelName(ref lineCopy, true);
            if (labelName.IsEmpty) return false;

            // Check for nonzero length and that label starts with letter or @ or _
            var simpleName = labelName.name;
            if (simpleName.Length == 0) return false;
            if (!char.IsLetter(simpleName[0]) && simpleName[0] != '@' && simpleName[0] != '_')
                return false;
            // Namespaced labels can't be local
            if (simpleName[0] == '@' && !string.IsNullOrEmpty(labelName.nspace)) return false; // Todo: produce a useful error instead of ignoring invalid syntax

            for (int i = 1; i < simpleName.Length; i++) { // i = 1 because we've already checked zero
                if (!char.IsLetter(simpleName[i]) && !char.IsDigit(simpleName[i]) && simpleName[i] != '_')
                    return false;
            }

            if (simpleName[0] == '@') { // Local label
                simpleName = simpleName.Substring(1); // Remove @
                string fullName = mostRecentNamedLabel + "." + simpleName.ToString(); // example: SomeFunction.LoopTop
                assembly.Labels.Add(new NamespacedLabel(fullName, null, iParsedLine, iSourceLine, true));
            } else { // Normal label
                assembly.Labels.Add(new NamespacedLabel(labelName.name, labelName.nspace, iParsedLine, iSourceLine, false));
            }
            line = lineCopy; //line.Substring(iColon + 1).TrimLeft();

            return true;
        }

        /// <summary>Gets the label that begins at position 0 within the specified string, and updates the string to remove the parsed label name.</summary>
        /// <param name="line">String to parse. Will be modified to remove the parsed label name.</param>
        /// <param name="checkForAssign">If true, the presence of a := symbol following a name will cause the name to NOT be parsed</param>
        /// <returns>A string containing a label, or an empty string.</returns>
        /// <remarks>Whitespace preceeding the label name will be cropped out.</remarks>
        private NamespacedLabelName GrabLabelName(ref StringSection line, bool checkForAssign) {
            var iColon = line.IndexOf(':');

            if (iColon == -1) return NamespacedLabelName.Empty;
            if (checkForAssign && (line.Length - 1 > iColon) && (line[iColon + 1] == '=')) {
                return NamespacedLabelName.Empty; // "x := y" is not a label
            }

            // Namespace::label
            if (line.Length > iColon + 1) {
                if (line[iColon + 1] == ':') { // '::' is a namespace operator
                    var nspace = line.Substring(iColon).Trim();
                    var restOfLine = line.Substring(iColon + 2);
                    var iColon2 = restOfLine.IndexOf(':');
                    if (iColon2 >= 0) {
                        if (checkForAssign && restOfLine.Length - 1 > iColon2 && restOfLine[iColon2 + 1] == '=') {
                            return NamespacedLabelName.Empty; // "n::x := y" is not a label
                        }

                        var label = restOfLine.Substring(iColon2).Trim();
                        line = restOfLine.Substring(iColon2 + 1);
                        return new NamespacedLabelName(label.ToString(), nspace.ToString());
                    } else {
                        return NamespacedLabelName.Empty;
                    }
                }
            }

            var result = line.Substring(0, iColon).Trim();
            line = line.Substring(iColon);
            return new NamespacedLabelName(result.ToString(), null);
        }

        //private struct labelName
        //{
        //    public labelName(StringSection nspace, StringSection name) {
        //        this.nspace = nspace;
        //        this.name = name;
        //    }
        //    public StringSection nspace;
        //    public StringSection name;
        //    public bool IsEmpty { get { return name.IsNullOrEmpty; } }
        //    public static readonly labelName Empty;
        //}

        private static void StripComments(ref StringSection line) {
            int iComment = line.IndexOf(';');
            if (iComment >= 0)
                line = line.Substring(0, iComment);
            line = line.Trim();
        }

        private static bool StringEquals(StringSection a, StringSection b, bool ignoreCase) {
            return StringSection.Compare(a, b, ignoreCase) == 0;
        }

        /// <summary>
        /// Returns a value between 0 and 255, or -1 if no opcode was found, or -2 if the addressing mode is not available for the instruction..
        /// </summary>
        /// <param name="instruction"></param>
        /// <param name="addressing"></param>
        /// <returns></returns>
        private int FindOpcode(StringSection instruction, Opcode.addressing addressing) {
            var ops = Opcode.allOps;
            bool instructionFound = false;
            bool foundInstructionInvalid = false;

            for (int i = 0; i < Opcode.allOps.Length; i++) {
                if (StringEquals(ops[i].name, instruction, true)) {
                    // Note that the instruction exists. We need to tell the user whether 
                    // an instruction does not exist or the desired addressing mode is not available.
                    instructionFound = true;

                    var addrMode = ops[i].addressing;

                    // Branch instructions will be treated as absolute until they are actually encoded.
                    if (addrMode == Opcode.addressing.relative) addrMode = Opcode.addressing.absolute;

                    if (addressing == addrMode) {
                        if (ops[i].valid | Assembler.AllowInvalidOpcodes)
                            return i;
                        else
                            foundInstructionInvalid = true;
                    }
                }
            }

            if (instructionFound) {
                if (foundInstructionInvalid) {
                    return (int)OpcodeError.InvalidOpcode;
                } else {
                    return (int)OpcodeError.InvalidAddressing;
                }
            } else {
                return (int)OpcodeError.UnknownInstruction;
            }
        }

        /// <summary>
        /// Identifies errors related to instructions and addressing modes.
        /// </summary>
        enum OpcodeError
        {
            /// <summary>
            /// There is no instruction by the specified name.
            /// </summary>
            UnknownInstruction = -1,
            /// <summary>
            /// The specified addressing mode is not supported for the instruction.
            /// </summary>
            InvalidAddressing = -2,
            /// <summary>
            /// The specified instruction or addressing mode exists but is not supported.
            /// </summary>
            InvalidOpcode = -3
        }
        bool ParseOperand(StringSection operand, out LiteralValue value, out string expression) {

            if (ExpressionEvaluator.TryParseLiteral(operand, out value)) {
                expression = null;
                return true;
            } else if (operand.Length > 0) {
                value = default(LiteralValue);
                expression = operand.ToString();
                return true;
            } else {
                value = default(LiteralValue);
                expression = null;
                return false;
            }
        }

        /// <summary>
        /// Returns the addressing mode for the operand. Zero-page addressing modes are not considered (this can be addressed when omitting opcodes).
        /// 'Accumulator' addressing is considered implied. The 'operand'
        /// paremeter is updated to remove any addressing characters.
        /// </summary>
        /// <param name="operand">The operand string. Must be trimmed.</param>
        /// <param name="addressing"></param>
        /// <returns></returns>
        private Opcode.addressing ParseAddressing(ref StringSection operand) {
            int opLen = operand.Length;

            // Accumulator or implied
            if (opLen == 0 || (opLen == 1 && Char.ToUpper(operand[0]) == 'A')) {
                return Opcode.addressing.implied;

            }

            // Immediate
            if (operand[0] == '#') {
                operand = operand.Substring(1).Trim();
                return Opcode.addressing.immediate;
            }

            // ,X
            if (char.ToUpper(operand[opLen - 1]) == 'X') {
                var sansX = operand.Substring(0, opLen - 1).TrimRight();
                if (sansX.Length > 0 && sansX[sansX.Length - 1] == ',') {
                    sansX = sansX.Substring(0, sansX.Length - 1).Trim();

                    // Update operand to remove addressing
                    operand = sansX;
                    return Opcode.addressing.absoluteIndexedX;
                }

            }
            // (),Y
            // ,Y
            if (char.ToUpper(operand[opLen - 1]) == 'Y') {
                var sansY = operand.Substring(0, opLen - 1).TrimRight();
                if (sansY.Length > 0 && sansY[sansY.Length - 1] == ',') {
                    sansY = sansY.Substring(0, sansY.Length - 1).Trim();

                    //(),Y
                    if (sansY.Length > 0 && sansY[0] == '(' && sansY[sansY.Length - 1] == ')') {
                        sansY = sansY.Substring(1, sansY.Length - 2).Trim();

                        operand = sansY;
                        return Opcode.addressing.indirectY;
                    }

                    operand = sansY;
                    return Opcode.addressing.absoluteIndexedY;
                }

            }

            // ()
            // (,X)
            if (operand[0] == '(' && operand[opLen - 1] == ')') {
                operand = operand.Substring(1, opLen - 2).Trim();
                opLen = operand.Length;

                // (,X)
                if (opLen > 0 && char.ToUpper(operand[opLen - 1]) == 'X') {
                    var sansX = operand.Substring(0, opLen - 1).TrimRight();
                    if (sansX.Length > 0 && sansX[sansX.Length - 1] == ',') {
                        sansX = sansX.Substring(0, sansX.Length - 1).Trim();

                        // Update operand to remove addressing
                        operand = sansX;
                        return Opcode.addressing.indirectX;
                    }

                }

                return Opcode.addressing.indirect;
            }


            ////}

            // If there are no addressing chars, it must be absolute (absolute vs. zero page is determined later)
            return Opcode.addressing.absolute;

        }

        bool EndsWith(StringSection text, string ending) {
            int diff = text.Length - ending.Length;
            if (diff < 0) return false;
            for (int i = 0; i < ending.Length; i++) {
                if (text[i + diff] != ending[i])
                    return false;
            }
            return true;
        }


    }




    /// <summary>
    /// Defines the interface used to get/set variable and label values.
    /// </summary>
    interface IValueNamespace
    {
        string CurrentNamespace { get; set; }

        int GetForwardLabel(int labelLevel, int iSourceLine);
        int GetBackwardLabel(int labelLevel, int iSourceLine);
        int GetForwardBrace(int labelLevel, int iSourceLine);
        int GetBackwardBrace(int labelLevel, int iSourceLine);

        void SetValue(NamespacedLabelName name, LiteralValue value, bool isFixed, out Error error);
        //void SetValue(StringSection name, StringSection nspace, LiteralValue value, bool isFixed, out Error error);
        LiteralValue GetValue(NamespacedLabelName name);
        //LiteralValue GetValue(StringSection name, StringSection nspace);
        //bool TryGetValue(StringSection name, out LiteralValue result);
        //bool TryGetValue(StringSection name, StringSection nspace, out LiteralValue result);
        bool TryGetValue(NamespacedLabelName name, out LiteralValue result);
    }

}
