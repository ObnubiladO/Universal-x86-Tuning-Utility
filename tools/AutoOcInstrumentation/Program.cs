using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Security.Cryptography;
using System.Text.Json;

if (args.Length != 3) throw new ArgumentException("Usage: AutoOcInstrumentation <original AutoOC.dll> <diagnostic UXTU.dll> <output AutoOC.dll>");
string originalPath = Path.GetFullPath(args[0]), hostPath = Path.GetFullPath(args[1]), outputPath = Path.GetFullPath(args[2]);
if (originalPath.Equals(outputPath, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Output must differ from original DLL.");
string originalHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(originalPath)));
if (originalHash != "E6B9CA8BC140D92A20B6D6F69F21A351813B26A82EB0CC9CF1379CA408CCBB8F")
    throw new InvalidOperationException("Only the audited AutoOC.dll from UXTU 26.3.1 is supported; review a different build before instrumenting it.");
using var assembly = AssemblyDefinition.ReadAssembly(originalPath);
using var host = AssemblyDefinition.ReadAssembly(hostPath);
var module = assembly.MainModule;
var monitor = module.Types.Single(t => t.FullName == "AutoOC.Monitors.InstabilityMonitor");
var trace = host.MainModule.Types.Single(t => t.FullName == "Universal_x86_Tuning_Utility.Scripts.Misc.AutoOcDiagnostics");
var setContext = module.ImportReference(trace.Methods.Single(m => m.Name == "SetSignalContext"));
var onSignal = module.ImportReference(trace.Methods.Single(m => m.Name == "OnSignal"));
if (monitor.Methods.Any(m => m.HasBody && m.Body.Instructions.Any(i => i.Operand is MethodReference r && r.DeclaringType.FullName == trace.FullName)))
    throw new InvalidOperationException("DLL is already instrumented.");
if (assembly.Name.HasPublicKey) throw new InvalidOperationException("Refusing to modify a signed assembly.");
var originalInstructions = monitor.Methods.Where(m => m.HasBody).ToDictionary(m => m, m => m.Body.Instructions.ToArray());
var originalOpcodes = originalInstructions.Values.SelectMany(x => x).ToDictionary(i => i, i => (i.OpCode, i.Operand));
var sites = new List<object>();
foreach (var (method, instructions) in originalInstructions)
{
    var il = method.Body.GetILProcessor();
    int ordinal = 0;
    foreach (var instruction in instructions)
    {
        if (instruction.OpCode != OpCodes.Call || instruction.Operand is not MethodReference target ||
            target.DeclaringType.FullName != monitor.FullName || target.Name is not ("SignalCpuInstability" or "SignalGpuInstability")) continue;
        string source = $"{method.Name}:{++ordinal}@IL_{instruction.Offset:x4}";
        var injected = new List<Instruction> { il.Create(OpCodes.Dup), il.Create(OpCodes.Ldstr, source) };
        bool hasEventArgs = method.Parameters.Count == 2 && method.Parameters[1].ParameterType.FullName.EndsWith("EventArgs", StringComparison.Ordinal);
        injected.Add(hasEventArgs ? il.Create(OpCodes.Ldarg, method.Parameters[1]) : il.Create(OpCodes.Ldnull));
        injected.Add(il.Create(OpCodes.Call, setContext));
        RedirectTargets(method, instruction, injected[0]);
        foreach (var added in injected) il.InsertBefore(instruction, added);
        sites.Add(new { source, signal = target.Name, eventArgumentsCaptured = hasEventArgs });
    }
}
foreach (string signalName in new[] { "SignalCpuInstability", "SignalGpuInstability" })
{
    var method = monitor.Methods.Single(m => m.Name == signalName);
    var il = method.Body.GetILProcessor();
    var first = method.Body.Instructions[0];
    il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
    il.InsertBefore(first, il.Create(OpCodes.Ldstr, signalName.Contains("Cpu") ? "cpu" : "igpu"));
    il.InsertBefore(first, il.Create(OpCodes.Call, onSignal));
    method.ImplAttributes |= MethodImplAttributes.NoInlining;
}
if (sites.Count == 0) throw new InvalidOperationException("No direct signal call sites found.");
// Existing instructions and semantic operands must remain intact. Only branch
// destinations that now include a diagnostic prelude may have been redirected.
foreach (var (method, instructions) in originalInstructions)
    foreach (var instruction in instructions)
    {
        if (!method.Body.Instructions.Contains(instruction) || instruction.OpCode != originalOpcodes[instruction].OpCode)
            throw new InvalidOperationException("An original instruction was removed or changed.");
        if (instruction.OpCode.OperandType is not (OperandType.InlineBrTarget or OperandType.ShortInlineBrTarget or OperandType.InlineSwitch) &&
            !Equals(instruction.Operand, originalOpcodes[instruction].Operand))
            throw new InvalidOperationException("An original semantic operand changed.");
    }
// Added preludes can push branch destinations beyond the short-branch range.
foreach (var method in monitor.Methods.Where(m => m.HasBody))
    foreach (var instruction in method.Body.Instructions)
        if (instruction.OpCode.OperandType == OperandType.ShortInlineBrTarget)
            instruction.OpCode = instruction.OpCode.Code switch
            {
                Code.Br_S => OpCodes.Br, Code.Brfalse_S => OpCodes.Brfalse, Code.Brtrue_S => OpCodes.Brtrue,
                Code.Beq_S => OpCodes.Beq, Code.Bge_S => OpCodes.Bge, Code.Bge_Un_S => OpCodes.Bge_Un,
                Code.Bgt_S => OpCodes.Bgt, Code.Bgt_Un_S => OpCodes.Bgt_Un, Code.Ble_S => OpCodes.Ble,
                Code.Ble_Un_S => OpCodes.Ble_Un, Code.Blt_S => OpCodes.Blt, Code.Blt_Un_S => OpCodes.Blt_Un,
                Code.Bne_Un_S => OpCodes.Bne_Un, Code.Leave_S => OpCodes.Leave,
                _ => throw new InvalidOperationException("Unknown short branch.")
            };
Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
assembly.Write(outputPath);
var manifest = new {
    originalSha256 = originalHash,
    instrumentedSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(outputPath))),
    directSignalCallSites = sites,
    centralSignalsInstrumented = 2,
    originalInstructionsPreserved = true,
    tuningRulesChanged = false
};
File.WriteAllText(outputPath + ".instrumentation.json", JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Instrumented {sites.Count} direct signal sites and both signal methods. Original instructions preserved.");

static void RedirectTargets(MethodDefinition method, Instruction original, Instruction replacement)
{
    foreach (var instruction in method.Body.Instructions)
    {
        if (ReferenceEquals(instruction.Operand, original)) instruction.Operand = replacement;
        else if (instruction.Operand is Instruction[] targets)
            for (int i = 0; i < targets.Length; i++) if (ReferenceEquals(targets[i], original)) targets[i] = replacement;
    }
    foreach (var handler in method.Body.ExceptionHandlers)
    {
        if (handler.TryStart == original) handler.TryStart = replacement;
        if (handler.TryEnd == original) handler.TryEnd = replacement;
        if (handler.HandlerStart == original) handler.HandlerStart = replacement;
        if (handler.HandlerEnd == original) handler.HandlerEnd = replacement;
        if (handler.FilterStart == original) handler.FilterStart = replacement;
    }
}
