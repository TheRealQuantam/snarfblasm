using System;
using System.Collections.Generic;
using System.Linq;
//using System.Windows.Forms;
using System.IO;
using snarfblasm;
using Romulus.Patch;
using Romulus.Plugin;
using System.Text;

namespace snarfblasm
{
    static class Program
    {
        static ProgramSwitches switches;
        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main(string[] args) {

            if (args.Length == 1 && args[0].ToUpper() == "-DEBUG") {
                args = Console.ReadLine().Split(',');
            }

            if (args.Length == 0|| string.IsNullOrEmpty(args[0].Trim())) {
                ShowHelp();
            } else {
                sourceFile = args[0].Trim();

                for (int i = 1; i < args.Length; i++) {
                    var arg = args[i];
                    bool isFirstArg = i == 1;
                    bool exit;

                    ProcessArg(arg, isFirstArg, out exit);
                    if(exit) return;
                }

                RunAssembler();
            }
        }

        private static void RunAssembler() {
            if (!FileReader.IsPseudoFile(sourceFile) && !fileSystem.FileExists(sourceFile)) {
                Console.WriteLine("Error: Source file does not exist.");
                return;
            }


            Assembler asm = new Assembler(sourceFile, fileSystem.GetFileText(sourceFile), fileSystem);
            destFile = GetDestFilePath(asm);

            if (switches.NewerOutput == OnOffSwitch.ON && File.Exists(destFile)) {
                DateTime sourceDate = File.GetLastWriteTime(sourceFile);
                DateTime destDate = File.GetLastWriteTime(destFile);
                if (destDate > sourceDate) {
                    Console.WriteLine("Notice: Result is newer than source, skipping {0:S}.", Path.GetFileName(sourceFile));
                    return;
                }
            }

            asm.OverflowChecking = switches.Checking ?? OverflowChecking.None;
            AddressLabels asmLabels = new AddressLabels();
            asm.Labels = asmLabels;

            asm.PhaseComplete += new EventHandler<Assembler.PhaseEventArgs>(asm_PhaseComplete);
            asm.PhaseStarted += new EventHandler<Assembler.PhaseEventArgs>(asm_PhaseStarted);

            var output = asm.Assemble();
            if (output == null) {
                ShowErrors(asm.GetErrors());
            } else {
                WriteAssemblerOutput(asm, output);
            }

            if (switches.DebugOutput == OnOffSwitch.ON) {
                WriteDebugOutput(asm);
            }
        }

        private static string GetDestFilePath(Assembler asm) {
            if (destFile == null) {
                bool isIPS = asm.HasPatchSegments;
                string outputExtension = isIPS ? ".ips" : ".bin";

                if (Path.GetExtension(sourceFile).Equals(outputExtension, StringComparison.InvariantCultureIgnoreCase)) {
                    // If the input file is stupidly named (.ips or .bin), we append the extension (e.g. input.bin.bin) so we don't overwrite the source.
                    destFile = sourceFile + outputExtension;
                } else {
                    if (isIPS) {
                        destFile = Path.ChangeExtension(sourceFile, ".ips");
                    } else {
                        destFile = Path.ChangeExtension(sourceFile, ".bin");
                    }
                }
            }

            return destFile;
        }

        private static void WriteAssemblerOutput(Assembler asm, byte[] output) {
            bool isPseudoFile;
            bool isIPS = asm.HasPatchSegments;
            string outputExtension = isIPS ? ".ips" : ".bin";

            if (destFile == null) {
                isPseudoFile = FileReader.IsPseudoFile(sourceFile);
                if (isPseudoFile) {
                    // Pseudo-files such as %input% and %clip% shouldn't get an extension-change
                    destFile = sourceFile;
                } else {
                    destFile = GetDestFilePath(asm);
                }
            } else {
                isPseudoFile = FileReader.IsPseudoFile(destFile);
            }

            if (asm.HasPatchSegments) {
                if (switches.PatchOffset != null) {
                    Console.WriteLine("Warning: Output type is IPS file. Offset argument will be ignored.");
                }

                var ipsFile = CreateIPSFile(output, asm.GetPatchSegments());
                fileSystem.WriteFile(destFile, ipsFile);
                Console.WriteLine(ipsFile.Length.ToString() + " bytes written to " + destFile);
            } else if (switches.PatchOffset == null) { // .BIN file
                fileSystem.WriteFile(destFile, output);
                ////File.WriteAllBytes(destFile, output);
                Console.WriteLine(output.Length.ToString() + " bytes written to " + destFile);
            } else { // Patch into another file
                using (var file = new FileStream(destFile, FileMode.Open, FileAccess.Write)) {
                    file.Seek((int)switches.PatchOffset, SeekOrigin.Begin);
                    file.Write(output, 0, output.Length);

                    Console.WriteLine(output.Length.ToString() + " bytes written to " + destFile + " at offset 0x" + ((int)switches.PatchOffset).ToString());
                }

            }
        }

        private static byte[] CreateIPSFile(byte[] output, IList<Romulus.PatchSegment> segments) {
            var ips = new IPS.Builder();

            for (int i = 0; i < segments.Count; i++) {
                var segment = segments[i];

                int srcStart = segment.Start;
                int srcLen = segment.Length;
                int destOffset = segment.PatchOffset;

                int destSize = Math.Min(srcLen, ushort.MaxValue);


                while (srcLen > 0) {
                    ips.AddRecord(output, srcStart, destSize, destOffset);

                    // If there was more data than would fit in one record, we write the remaining data in another record
                    srcStart += destSize;
                    destOffset += destSize;
                    srcLen -= destSize;

                    destSize = Math.Min(srcLen, ushort.MaxValue);
                }
            }

            return ips.CreateIPS();
        }

        private static void WriteDebugOutput(Assembler asm) {
            string debugFile = Path.ChangeExtension(sourceFile, ".mlb");

            StreamWriter stream = new StreamWriter(debugFile);
            stream.Write(Encoding.ASCII.GetString(asm.Labels.Ram.BuildDebugFile(-1)));

            List<IBankLabels> banks = asm.Labels.Banks.GetBanks();
            foreach(IBankLabels bank in banks) {
                stream.Write(Encoding.ASCII.GetString(bank.BuildDebugFile(bank.BankIndex)));
            }

            Console.WriteLine("Debug file written to {0:S}", debugFile);
            stream.Close();
        }

        static void asm_PhaseStarted(object sender, Assembler.PhaseEventArgs e) {
            Console.Write(e.Message);
        }
        static void asm_PhaseComplete(object sender, Assembler.PhaseEventArgs e) {
            Console.WriteLine(e.Message);

        }
        private static void ShowErrors(IList<ErrorDetail> errors) {
            Console.WriteLine();
            Console.WriteLine();

            for (int i = 0; i < errors.Count; i++) {
                var error = errors[i];
                Console.WriteLine(error.File + " (" + error.LineNumber.ToString() + ") " + error.Code.ToString().Replace('_',' ') + ": " + error.Message);
            }
            Console.WriteLine("Assemble failed.");
        }


        #region Command line parsing
        private static void ProcessArg(string arg, bool isFirstArg, out bool exit) {
            exit = false;

            if (arg.Length != 0) {
                if (arg[0] == '-') {
                    ProcessSwitch(arg, out exit);
                    if (exit) {
                        ShowHelp();
                        return;
                    }
                } else if (isFirstArg) {
                    ProcessDest(arg);
                } else {
                    Console.WriteLine("Unrecognized parameter.");
                    ShowHelp();
                    exit = true;
                    return;
                }
            }
            return;
        }

        private static bool ProcessDest(string arg) {
            if (destFile != null) {
                Console.WriteLine("Can not specify more than one dest file.");
                return false;
            }

            destFile = arg;
            return true;
        }

        //private static bool ProcessOffset(string arg) {
        //    if (offset != null) {
        //        Console.WriteLine("Can not specify more than one offset.");
        //        return false;
        //    }

        //    bool hex;
        //    if (arg.Length > 1 && arg[1] == '$') {
        //        hex = true;
        //        arg = arg.Substring(2);
        //    } else {
        //        hex = false;
        //        arg = arg.Substring(1);
        //    }

        //    System.Globalization.NumberStyles style = hex ? System.Globalization.NumberStyles.HexNumber : System.Globalization.NumberStyles.Integer;
        //    int offsetValue;
        //    if (int.TryParse(arg, style, null, out offsetValue)) {
        //        offset = offsetValue;
        //        return true;
        //    } else {
        //        Console.WriteLine("Invalid offset value.");
        //        return false;
        //    }
        //}

        private static bool ProcessSwitch(string arg, out bool error) {
            error = false;

            // Parse out switch name and parameter, and remove leading "-"
            string switchName;
            string switchValue = null;

            int iColon = arg.IndexOf(':');
            if (iColon > 0) {
                switchName = arg.Substring(1, iColon - 1).ToUpper();
                switchValue = arg.Substring(iColon + 1);
            } else {
                switchName = arg.Substring(1).ToUpper();
            }

            switch (switchName) {
                case "OFFSET":
                    if (switches.PatchOffset == null) {
                        ParseOffset(switchValue, out error);
                    } else {
                        ShowDuplicateSwitchError(switchName);
                        error = true;
                    }
                    break;
                case "CHECKING":
                    if (switches.Checking == null) {
                        ParseChecking(switchValue, out error);
                    } else {
                        ShowDuplicateSwitchError(switchName);
                        error = true;
                    }
                    break;
                case "INVALID":
                    if (switches.InvalidOpsAllowed == null) {
                        if (switchValue == null)
                            switches.InvalidOpsAllowed = OnOffSwitch.ON;
                        else
                            switches.InvalidOpsAllowed = ParseOnOff(switchName, switchValue, out error);
                    } else {
                        ShowDuplicateSwitchError(switchName);
                        error = true;
                    }
                    break;
                case "NEWER":
                    if (switches.NewerOutput == null) {
                        if (switchValue == null)
                            switches.NewerOutput = OnOffSwitch.ON;
                        else
                            switches.NewerOutput = ParseOnOff(switchName, switchValue, out error);
                    } else {
                        ShowDuplicateSwitchError(switchName);
                        error = true;
                    }
                    break;
                case "DBG":
                    if (switches.DebugOutput == null) {
                        if (switchValue == null)
                            switches.DebugOutput = OnOffSwitch.ON;
                        else
                            switches.DebugOutput = ParseOnOff(switchName, switchValue, out error);
                    } else {
                        ShowDuplicateSwitchError(switchName);
                        error = true;
                    }
                    break;
                default:
                    Console.WriteLine("Invalid switch: " + arg);
                    Console.WriteLine();
                    ShowHelp();
                    error = true;
                    break;
            }

            return true;
        }

        private static OnOffSwitch? ParseOnOff(string switchName, string value, out bool invalid) {
            invalid = false;

            if (value.Equals("ON", StringComparison.InvariantCultureIgnoreCase)) {
                return OnOffSwitch.ON;
            } else if (value.Equals("OFF", StringComparison.InvariantCultureIgnoreCase)) {
                return OnOffSwitch.OFF;
            } else {
                Console.WriteLine("Value " + value + " is invalid for -" + switchName);
                Console.WriteLine();
                ShowHelp();

                invalid = true;
                return null;
            }
        }

        private static void ParseChecking(string value, out bool invalid) {
            invalid = false;

            switch (value.ToUpper()) {
                case "ON":
                    switches.Checking = OverflowChecking.Unsigned;
                    break;
                case "OFF":
                    switches.Checking = OverflowChecking.None;
                    break;
                case "SIGNED":
                    switches.Checking = OverflowChecking.Signed;
                    break;
                default:
                    Console.WriteLine("Value " + value + " is invalid for -CHECKING");
                    Console.WriteLine();
                    ShowHelp();

                    invalid = true;
                    break;
            }
        }

        private static void ParseOffset(string value, out bool invalid) {
            invalid = false;

            if (value.Length == 0) {
                invalid = true;
                return;
            }

            bool hex = false;
            if (value.StartsWith("$")) {
                hex = true;
                value = value.Substring(1);
            } else if (value.StartsWith("0x", StringComparison.InvariantCultureIgnoreCase)) {
                hex = true;
                value = value.Substring(2);
            }

            int offset;
            bool valid;
            if (hex) {
                valid = int.TryParse(value, System.Globalization.NumberStyles.HexNumber, null, out offset);
            } else {
                valid = int.TryParse(value, out offset);
            }

            if (valid)
                switches.PatchOffset = offset;
            else {
                Console.WriteLine("Invalid patch offset specified.");
                Console.WriteLine();
                ShowHelp();
            }
        }

        private static void ShowDuplicateSwitchError(string swicth) {
            Console.WriteLine("Duplicate switch: " + swicth);
            Console.WriteLine();
            ShowHelp();
        }
        #endregion

        const string HelpText =
@"snarfblASM 6502 assembler - syntax
    snarfblasm sourceFile [destFile] [switches]

    switches:
        -CHECKING:OFF/ON/SIGNED
            Overflow checking in expressions
        -DBG[:OFF/ON]
            Produce a Mesen 2 mlb symbol file
        -INVALID[:OFF/ON]
            Invalid opcodes are allowed (ON)
        -NEWER[:OFF/ON]
            Compiles only if source file is newer than destination (OFF)
        -OFFSET:value
            Patch bin output to the destination file at the specified offset.
            Value should be an integer or a hex value formatted as $FF or 0xFF.

    An IPS patch file will be output automatically if patch segments are found.
    Otherwise a raw binary file will be output.

    Example: snarfblasm source.asm -CHECKING:ON
";
        static bool helpShown = false;

        private static void ShowHelp() {
            if (!helpShown)
              Console.WriteLine(HelpText);
            helpShown = true;
        }

        static FileReader fileSystem = new FileReader();

        static string sourceFile;
        static string destFile;

        class FileReader : IFileSystem
        {
            /*public const string Psuedo_Form = "%form%";
            public const string Pseudo_Clip = "%clip%";*/

            #region IFileSystem Members

            public string GetFileText(string filename) {
                /*if (filename.Equals(Psuedo_Form, StringComparison.InvariantCultureIgnoreCase)) {
                    return snarfblasm.TextForm.GetText();
                } else if (filename.Equals(Pseudo_Clip, StringComparison.InvariantCultureIgnoreCase)) {
                    return Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty;
                } else*/ {
                    return System.IO.File.ReadAllText(filename);
                }
            }

            public void WriteFile(string filename, byte[] data) {
                /*if (filename.Equals(Psuedo_Form, StringComparison.InvariantCultureIgnoreCase)) {
                    TextForm.GetText(Romulus.Hex.FormatHex(data));
                } else if (filename.Equals(Pseudo_Clip, StringComparison.InvariantCultureIgnoreCase)) {
                    if (data.Length == 0)
                        Clipboard.SetText(" ");
                    else
                        Clipboard.SetText(Romulus.Hex.FormatHex(data));
                } else*/ {
                    File.WriteAllBytes(filename, data);
                }
            }

            #endregion

            public static bool IsPseudoFile(string file) {
                if (file == null) return false;
                if (file.Length < 3) return false;
                if (file[0] == '%' && file[file.Length - 1] == '%') return true;
                return false;
            }

            #region IFileSystem Members


            public long GetFileSize(string filename) {
                //if (filename.Equals(Psuedo_Form, StringComparison.InvariantCultureIgnoreCase)) return 0;

                //   System.Security.SecurityException:
                //     The caller does not have the required permission.
                //
                //   System.ArgumentException:
                //     The file name is empty, contains only white spaces, or contains invalid characters.
                //
                //   System.UnauthorizedAccessException:
                //     Access to fileName is denied.
                //
                //   System.IO.PathTooLongException:
                //     The specified path, file name, or both exceed the system-defined maximum
                //     length. For example, on Windows-based platforms, paths must be less than
                //     248 characters, and file names must be less than 260 characters.
                //
                //   System.NotSupportedException:
                //     fileName contains a colon (:) in the middle of the string.
                try {
                    return new FileInfo(filename).Length;
                } catch (System.Security.SecurityException) {
                    return -1;
                } catch (System.IO.IOException) {
                    return -1;
                } catch (ArgumentException) {
                    return -1;
                } catch (UnauthorizedAccessException) {
                    return -1;
                }
            }

            public Stream GetFileReadStream(string filename) {
                return new FileStream(filename, FileMode.Open);
            }

            public bool FileExists(string name) {
                //if (name.Equals(Psuedo_Form, StringComparison.InvariantCultureIgnoreCase)) return true;

                return File.Exists(name);
            }

            #endregion

        }

    }

    struct ProgramSwitches
    {
        public OverflowChecking? Checking;
        public OnOffSwitch? DebugOutput;
        public OnOffSwitch? InvalidOpsAllowed;
        public OnOffSwitch? NewerOutput;
        public int? PatchOffset;
    }

    enum OnOffSwitch
    {
        ON,
        OFF
    }
}
