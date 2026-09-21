using System.Reflection;
using System.Text;
using System.Text.Json;

return Deploy(args);

static int Deploy(string[] args)
{
    if (args.Length != 2)
    {
        Console.Error.WriteLine("Usage: Luban.Extension.Deployer <extension.dll> <Luban directory>");
        return 1;
    }

    string extensionDll = Path.GetFullPath(args[0]);
    string lubanDir = Path.GetFullPath(args[1]);
    if (!File.Exists(extensionDll))
    {
        Console.Error.WriteLine($"Extension DLL was not found: {extensionDll}");
        return 1;
    }

    if (!Directory.Exists(lubanDir))
    {
        Console.Error.WriteLine($"Luban directory was not found: {lubanDir}");
        return 1;
    }

    string manifestPath = Path.Combine(lubanDir, "Luban.deps.json");
    if (!File.Exists(manifestPath))
    {
        Console.Error.WriteLine($"Luban dependency manifest was not found: {manifestPath}");
        return 1;
    }

    AssemblyName assembly = AssemblyName.GetAssemblyName(extensionDll);
    string assemblyName = assembly.Name ?? throw new InvalidOperationException("The extension assembly has no name.");
    string assemblyVersion = assembly.Version?.ToString() ?? "1.0.0.0";
    string libraryName = $"{assemblyName}/{assemblyVersion}";

    string destinationDll = Path.Combine(lubanDir, Path.GetFileName(extensionDll));
    File.Copy(extensionDll, destinationDll, overwrite: true);

    bool manifestChanged = RegisterExtension(manifestPath, libraryName, assemblyName, assemblyVersion);
    Console.WriteLine($"Deployed: {destinationDll}");
    Console.WriteLine(manifestChanged
        ? $"Registered extension in: {manifestPath}"
        : "Extension is already registered; Luban.deps.json was left unchanged.");
    return 0;
}

static bool RegisterExtension(string manifestPath, string libraryName, string assemblyName, string assemblyVersion)
{
    string json = File.ReadAllText(manifestPath, Encoding.UTF8);
    using JsonDocument document = JsonDocument.Parse(json);
    JsonElement root = document.RootElement;
    string targetName = root.GetProperty("runtimeTarget").GetProperty("name").GetString()
        ?? throw new InvalidOperationException("Luban.deps.json has no runtime target name.");

    JsonElement targets = root.GetProperty("targets");
    JsonElement libraries = root.GetProperty("libraries");
    if (!targets.TryGetProperty(targetName, out JsonElement target))
    {
        throw new InvalidOperationException($"Target '{targetName}' was not found in {manifestPath}");
    }

    bool hasTargetEntry = target.TryGetProperty(libraryName, out _);
    bool hasLibraryEntry = libraries.TryGetProperty(libraryName, out _);
    if (hasTargetEntry && hasLibraryEntry)
    {
        return false;
    }

    string updated = json;
    if (!hasTargetEntry)
    {
        updated = InsertProperty(
            updated,
            targetName,
            libraryName,
            (indent, unit, newline) => BuildTargetEntry(assemblyName, assemblyVersion, indent, unit, newline));
    }

    if (!hasLibraryEntry)
    {
        updated = InsertProperty(
            updated,
            "libraries",
            libraryName,
            (_, _, _) => "{\"type\":\"project\",\"serviceable\":false,\"sha512\":\"\"}");
    }

    // Validate the locally edited text before replacing the manifest. Existing
    // whitespace is preserved because only the missing property is inserted.
    using JsonDocument validationDocument = JsonDocument.Parse(updated);
    File.WriteAllText(manifestPath, updated, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    return true;
}

static string BuildTargetEntry(string assemblyName, string assemblyVersion, string indent, string unit, string newline)
{
    string nested = indent + unit;
    string asset = nested + unit;
    string value = asset + unit;
    string assemblyFileName = JsonSerializer.Serialize($"{assemblyName}.dll");
    string version = JsonSerializer.Serialize(assemblyVersion);
    return string.Join(newline,
        "{",
        $"{nested}\"runtime\": {{",
        $"{asset}{assemblyFileName}: {{",
        $"{value}\"assemblyVersion\": {version},",
        $"{value}\"fileVersion\": {version}",
        $"{asset}}}",
        $"{nested}}}",
        $"{indent}}}");
}

static string InsertProperty(
    string json,
    string objectName,
    string propertyName,
    Func<string, string, string, string> createValue)
{
    int objectStart = FindObjectStart(json, objectName);
    int objectEnd = FindMatchingBrace(json, objectStart);
    string newline = json.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
    string closingIndent = GetLineIndent(json, objectEnd);
    string childIndent = FindChildIndent(json, objectStart, objectEnd, closingIndent, newline);
    string indentUnit = childIndent.StartsWith(closingIndent, StringComparison.Ordinal) && childIndent.Length > closingIndent.Length
        ? childIndent[closingIndent.Length..]
        : "  ";
    string property = $"{JsonSerializer.Serialize(propertyName)}: {createValue(childIndent, indentUnit, newline)}";
    bool hasProperties = json[(objectStart + 1)..objectEnd].Any(c => !char.IsWhiteSpace(c));
    string insertion = (hasProperties ? "," : string.Empty) + newline + childIndent + property + newline + closingIndent;
    return json.Insert(objectEnd, insertion);
}

static int FindObjectStart(string json, string propertyName)
{
    for (int index = 0; index < json.Length; index++)
    {
        if (json[index] != '"')
        {
            continue;
        }

        int endQuote = FindStringEnd(json, index);
        string name = JsonSerializer.Deserialize<string>(json[index..(endQuote + 1)])!;
        int cursor = SkipWhitespace(json, endQuote + 1);
        if (name == propertyName && cursor < json.Length && json[cursor] == ':')
        {
            cursor = SkipWhitespace(json, cursor + 1);
            if (cursor < json.Length && json[cursor] == '{')
            {
                return cursor;
            }
        }

        index = endQuote;
    }

    throw new InvalidOperationException($"JSON object '{propertyName}' was not found.");
}

static int FindMatchingBrace(string json, int objectStart)
{
    int depth = 0;
    for (int index = objectStart; index < json.Length; index++)
    {
        if (json[index] == '"')
        {
            index = FindStringEnd(json, index);
            continue;
        }

        if (json[index] == '{') depth++;
        if (json[index] == '}' && --depth == 0) return index;
    }

    throw new InvalidOperationException("JSON object is not closed.");
}

static int FindStringEnd(string json, int quoteStart)
{
    for (int index = quoteStart + 1; index < json.Length; index++)
    {
        if (json[index] == '\\')
        {
            index++;
            continue;
        }

        if (json[index] == '"') return index;
    }

    throw new InvalidOperationException("JSON string is not closed.");
}

static int SkipWhitespace(string text, int start)
{
    while (start < text.Length && char.IsWhiteSpace(text[start])) start++;
    return start;
}

static string GetLineIndent(string text, int position)
{
    int lineStart = text.LastIndexOf('\n', position - 1) + 1;
    return text[lineStart..position];
}

static string FindChildIndent(string text, int objectStart, int objectEnd, string closingIndent, string newline)
{
    int firstLineStart = text.IndexOf(newline, objectStart, StringComparison.Ordinal);
    if (firstLineStart >= 0 && firstLineStart < objectEnd)
    {
        firstLineStart += newline.Length;
        int firstContent = SkipWhitespace(text, firstLineStart);
        if (firstContent < objectEnd)
        {
            return text[firstLineStart..firstContent];
        }
    }

    return closingIndent + "  ";
}
