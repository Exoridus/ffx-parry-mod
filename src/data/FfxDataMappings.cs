namespace Fahrenheit.Mods.Parry;

internal sealed class FfxDataMappings
{
    /// <summary>
    ///     Schema version this consumer is built against. Mirrors the
    ///     <c>SchemaVersion</c> field the pipeline writes into the runtime bundle
    ///     header (see <c>ffx-knowledge-base/build/Build.BuildModRuntimeBundles.cs</c>).
    ///     A mismatch is logged but does not abort the load — the consumer reads
    ///     fields defensively, so unknown future fields are ignored and missing
    ///     legacy fields fall back to empty/zero. The warning surfaces drift early.
    /// </summary>
    private const int ExpectedBundleSchemaVersion = 1;

    private static readonly Regex _commandDumpRegex = new(
        pattern: @"^\s*(?<id>[0-9A-Fa-f]{4})\s*\(Offset\s*[0-9A-Fa-f]+\)\s*-\s*(?<payload>.+)$",
        options: RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly string[] _commandDumpNames = [
        "READ_ALL_COMMANDS.txt",
        "read_all_commands.txt",
        "commands_dump.txt",
        "commands.txt"
    ];
    private static readonly string[] _legacyCompactJsonNames = [
        "fhparry.mappings.json"
    ];

    private sealed class CommandLikeRecord
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string DamageType { get; set; } = string.Empty;

        /// <summary>
        ///     Additive per-command mechanics block (hit_count, formula_id, targeting[], etc.)
        ///     carried verbatim from the runtime bundle's <c>Domains.Commands.&lt;id&gt;.Mechanics</c>.
        ///     Null when the bundle predates this field or the entry has none.
        /// </summary>
        public JsonElement? Mechanics { get; set; }

        /// <summary>
        ///     The command's <c>hit_count</c>, promoted to a typed field and parsed
        ///     once at load so the hot combat path (counter gating) reads an int
        ///     instead of navigating <see cref="Mechanics"/> on every query.
        ///     0 = unknown / non-numeric / null (the ~150 non-damaging records).
        ///     The full <see cref="Mechanics"/> blob is still kept for the debug UI.
        /// </summary>
        public int HitCount { get; set; }
    }

    private sealed class MonsterRecord
    {
        public string Name { get; set; } = string.Empty;
        public string Sensor { get; set; } = string.Empty;
        public string Scan { get; set; } = string.Empty;
    }

    private readonly Dictionary<ushort, CommandLikeRecord> _commands = new();
    private readonly Dictionary<ushort, CommandLikeRecord> _autoAbilities = new();
    private readonly Dictionary<ushort, CommandLikeRecord> _keyItems = new();
    private readonly Dictionary<ushort, string> _symbolicCommands = new();
    private readonly Dictionary<int, MonsterRecord> _monsters = new();
    private readonly Dictionary<string, SortedDictionary<int, string>> _events = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SortedDictionary<int, string>> _battles = new(StringComparer.OrdinalIgnoreCase);

    private readonly List<string> _sources = [];
    private string _preferredLocale = "us";

    public int CommandCount => _commands.Count;
    public int AutoAbilityCount => _autoAbilities.Count;
    public int KeyItemCount => _keyItems.Count;
    public int SymbolicCommandCount => _symbolicCommands.Count;
    public int MonsterCount => _monsters.Count;
    public int EventCount => _events.Count;
    public int BattleCount => _battles.Count;
    public bool HasAny => CommandCount + AutoAbilityCount + KeyItemCount + MonsterCount + EventCount + BattleCount > 0;
    public string SourceSummary => _sources.Count == 0 ? "None" : string.Join(", ", _sources);

    public void LoadFromDirectories(
        IEnumerable<string> directories,
        string preferredLocale,
        Action<string>? infoLog = null,
        Action<string>? warnLog = null)
    {
        clear();

        _preferredLocale = normalize_locale(preferredLocale);

        foreach (string rawPath in directories)
        {
            string path = normalize_path(rawPath);
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                continue;
            }

            bool loadedSomething = false;
            loadedSomething |= try_load_command_dump(path, infoLog, warnLog);
            loadedSomething |= try_load_compact_json(path, infoLog, warnLog);

            if (loadedSomething)
            {
                _sources.Add(path);
            }
        }
    }

    public bool TryResolveCommandDisplay(uint lastComRaw, out string label, out string kind)
    {
        ushort id = (ushort)(lastComRaw & 0xFFFF);
        return TryResolveCommandDisplay(id, out label, out kind);
    }

    public bool TryResolveCommandDisplay(ushort commandId, out string label, out string kind)
    {
        label = string.Empty;
        kind = string.Empty;
        ushort id = commandId;
        if (id == 0) return false;

        if (_commands.TryGetValue(id, out CommandLikeRecord? cmd) && !string.IsNullOrWhiteSpace(cmd.Name))
        {
            label = cmd.Name;
            kind = "Command";
            return true;
        }

        if (_autoAbilities.TryGetValue(id, out CommandLikeRecord? autoAbility) && !string.IsNullOrWhiteSpace(autoAbility.Name))
        {
            label = autoAbility.Name;
            kind = "AutoAbility";
            return true;
        }

        if (_keyItems.TryGetValue(id, out CommandLikeRecord? keyItem) && !string.IsNullOrWhiteSpace(keyItem.Name))
        {
            label = keyItem.Name;
            kind = "KeyItem";
            return true;
        }

        if (_symbolicCommands.TryGetValue(id, out string? symbol) && !string.IsNullOrWhiteSpace(symbol))
        {
            label = symbol;
            kind = "Symbol";
            return true;
        }

        return false;
    }

    public bool TryResolveCommandDamageType(ushort commandId, out string damageType)
    {
        damageType = string.Empty;
        if (commandId == 0) return false;
        if (_commands.TryGetValue(commandId, out CommandLikeRecord? cmd) && !string.IsNullOrWhiteSpace(cmd.DamageType))
        {
            damageType = cmd.DamageType;
            return true;
        }

        return false;
    }

    public bool TryGetCommandMechanics(ushort id, out JsonElement mechanics)
    {
        if (_commands.TryGetValue(id, out CommandLikeRecord? cmd) && cmd.Mechanics.HasValue)
        {
            mechanics = cmd.Mechanics.Value;
            return true;
        }

        mechanics = default;
        return false;
    }

    /// <summary>
    ///     Reads the additive <c>hit_count</c> from a command's mechanics block
    ///     (runtime bundle <c>Domains.Commands.&lt;id&gt;.Mechanics.hit_count</c>).
    ///     Returns <c>false</c> when the command is unknown, carries no mechanics,
    ///     or has a null/non-numeric hit_count (the ~150 non-damaging / unparsed
    ///     records) — callers treat that as "no multi-hit signal".
    /// </summary>
    public bool TryGetCommandHitCount(ushort id, out int hitCount)
    {
        if (_commands.TryGetValue(id, out CommandLikeRecord? cmd) && cmd.HitCount > 0)
        {
            hitCount = cmd.HitCount;
            return true;
        }

        hitCount = 0;
        return false;
    }

    public bool TryResolveMonsterName(int monsterLikeId, out string name)
    {
        name = string.Empty;
        foreach (int candidate in enumerate_monster_id_candidates(monsterLikeId))
        {
            if (_monsters.TryGetValue(candidate, out MonsterRecord? record) && !string.IsNullOrWhiteSpace(record.Name))
            {
                name = record.Name;
                return true;
            }
        }

        return false;
    }

    public bool TryResolveMonsterSensor(int monsterLikeId, out string sensor)
    {
        sensor = string.Empty;
        foreach (int candidate in enumerate_monster_id_candidates(monsterLikeId))
        {
            if (_monsters.TryGetValue(candidate, out MonsterRecord? record) && !string.IsNullOrWhiteSpace(record.Sensor))
            {
                sensor = record.Sensor;
                return true;
            }
        }

        return false;
    }

    public bool TryResolveMonsterScan(int monsterLikeId, out string scan)
    {
        scan = string.Empty;
        foreach (int candidate in enumerate_monster_id_candidates(monsterLikeId))
        {
            if (_monsters.TryGetValue(candidate, out MonsterRecord? record) && !string.IsNullOrWhiteSpace(record.Scan))
            {
                scan = record.Scan;
                return true;
            }
        }

        return false;
    }

    public bool TryResolveBattleLabel(string battleId, out string label) =>
        try_resolve_script_label(_battles, battleId, out label);

    public bool TryResolveEventLabel(string eventId, out string label) =>
        try_resolve_script_label(_events, eventId, out label);

    private static bool try_resolve_script_label(
        Dictionary<string, SortedDictionary<int, string>> source,
        string id,
        out string label)
    {
        label = string.Empty;
        string key = normalize_script_id(id);
        if (string.IsNullOrWhiteSpace(key)) return false;
        if (!source.TryGetValue(key, out SortedDictionary<int, string>? entries) || entries.Count == 0) return false;

        foreach ((int _, string value) in entries)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                label = value;
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<int> enumerate_monster_id_candidates(int raw)
    {
        if (raw < 0) yield break;

        // Different runtime surfaces may expose different flavors of monster id.
        // These candidates are ordered by confidence.
        yield return raw;

        int low12 = raw & 0x0FFF;
        if (low12 != raw) yield return low12;

        int low10 = raw & 0x03FF;
        if (low10 != raw && low10 != low12) yield return low10;
    }

    private bool try_load_command_like(
        string path,
        Dictionary<ushort, CommandLikeRecord> destination,
        Action<string>? infoLog,
        Action<string>? warnLog)
    {
        if (!File.Exists(path)) return false;

        List<string[]> rows = read_csv(path, warnLog);
        if (rows.Count == 0) return false;

        Dictionary<string, int> header = map_header(rows[0]);
        int idIdx = get_index(header, "id");
        int typeIdx = get_index(header, "type");
        int copyIdx = get_index(header, "direct copy", "directcopy");

        if (idIdx < 0)
        {
            warnLog?.Invoke($"Skipping {path}: missing 'id' column.");
            return false;
        }

        var deferredCopies = new List<(ushort id, string type, ushort copyFrom)>();
        for (int i = 1; i < rows.Count; i++)
        {
            string[] row = rows[i];
            if (!try_parse_hex(read_cell(row, idIdx), out ushort id)) continue;

            string type = read_cell(row, typeIdx).Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(type)) type = "name";

            string localized = pick_localized_text(row, header);
            string directCopy = read_cell(row, copyIdx);

            CommandLikeRecord record = get_or_create(destination, id);
            if (!string.IsNullOrWhiteSpace(localized))
            {
                if (type == "description") record.Description = localized;
                else record.Name = localized;
            }
            else if (try_parse_hex(directCopy, out ushort copyFrom) && copyFrom != id)
            {
                deferredCopies.Add((id, type, copyFrom));
            }
        }

        for (int i = 0; i < deferredCopies.Count; i++)
        {
            (ushort id, string type, ushort copyFrom) = deferredCopies[i];
            if (!destination.TryGetValue(copyFrom, out CommandLikeRecord? source)) continue;
            CommandLikeRecord dest = get_or_create(destination, id);
            if (type == "description")
            {
                if (string.IsNullOrWhiteSpace(dest.Description)) dest.Description = source.Description;
            }
            else
            {
                if (string.IsNullOrWhiteSpace(dest.Name)) dest.Name = source.Name;
            }
        }

        infoLog?.Invoke($"Loaded {destination.Count} entries from {Path.GetFileName(path)}.");
        return true;
    }

    private bool try_load_monsters(string path, Action<string>? infoLog, Action<string>? warnLog)
    {
        if (!File.Exists(path)) return false;

        List<string[]> rows = read_csv(path, warnLog);
        if (rows.Count == 0) return false;

        Dictionary<string, int> header = map_header(rows[0]);
        int idIdx = get_index(header, "id");
        int typeIdx = get_index(header, "type");
        int copyIdx = get_index(header, "direct copy", "directcopy");

        if (idIdx < 0 || typeIdx < 0)
        {
            warnLog?.Invoke($"Skipping {path}: missing 'id' or 'type' column.");
            return false;
        }

        var deferredCopies = new List<(int id, string type, int copyFrom)>();
        for (int i = 1; i < rows.Count; i++)
        {
            string[] row = rows[i];
            if (!int.TryParse(read_cell(row, idIdx), NumberStyles.Integer, CultureInfo.InvariantCulture, out int id)) continue;

            string type = read_cell(row, typeIdx).Trim().ToLowerInvariant();
            if (type == "scan") type = "scan";
            else if (type == "sensor") type = "sensor";
            else type = "name";

            string localized = pick_localized_text(row, header);
            string directCopy = read_cell(row, copyIdx);

            MonsterRecord record = get_or_create(_monsters, id);
            if (!string.IsNullOrWhiteSpace(localized))
            {
                if (type == "sensor") record.Sensor = localized;
                else if (type == "scan") record.Scan = localized;
                else record.Name = localized;
            }
            else if (int.TryParse(directCopy, NumberStyles.Integer, CultureInfo.InvariantCulture, out int copyFrom) && copyFrom != id)
            {
                deferredCopies.Add((id, type, copyFrom));
            }
        }

        for (int i = 0; i < deferredCopies.Count; i++)
        {
            (int id, string type, int copyFrom) = deferredCopies[i];
            if (!_monsters.TryGetValue(copyFrom, out MonsterRecord? source)) continue;
            MonsterRecord dest = get_or_create(_monsters, id);
            if (type == "sensor")
            {
                if (string.IsNullOrWhiteSpace(dest.Sensor)) dest.Sensor = source.Sensor;
            }
            else if (type == "scan")
            {
                if (string.IsNullOrWhiteSpace(dest.Scan)) dest.Scan = source.Scan;
            }
            else
            {
                if (string.IsNullOrWhiteSpace(dest.Name)) dest.Name = source.Name;
            }
        }

        infoLog?.Invoke($"Loaded {_monsters.Count} monster entries from {Path.GetFileName(path)}.");
        return true;
    }

    private bool try_load_script_lines(
        string path,
        Dictionary<string, SortedDictionary<int, string>> destination,
        Action<string>? infoLog,
        Action<string>? warnLog)
    {
        if (!File.Exists(path)) return false;

        List<string[]> rows = read_csv(path, warnLog);
        if (rows.Count == 0) return false;

        Dictionary<string, int> header = map_header(rows[0]);
        int idIdx = get_index(header, "id");
        int stringIdx = get_index(header, "string index", "stringindex");
        if (idIdx < 0 || stringIdx < 0)
        {
            warnLog?.Invoke($"Skipping {path}: missing 'id' or 'string index' column.");
            return false;
        }

        for (int i = 1; i < rows.Count; i++)
        {
            string[] row = rows[i];
            string id = normalize_script_id(read_cell(row, idIdx));
            if (string.IsNullOrWhiteSpace(id)) continue;
            if (!int.TryParse(read_cell(row, stringIdx), NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx)) continue;

            string localized = pick_localized_text(row, header);
            if (string.IsNullOrWhiteSpace(localized)) continue;

            if (!destination.TryGetValue(id, out SortedDictionary<int, string>? texts))
            {
                texts = new SortedDictionary<int, string>();
                destination[id] = texts;
            }

            texts[idx] = localized;
        }

        infoLog?.Invoke($"Loaded {destination.Count} entries from {Path.GetFileName(path)}.");
        return true;
    }

    private bool try_load_command_dump(string directoryPath, Action<string>? infoLog, Action<string>? warnLog)
    {
        string[] files = _commandDumpNames
            .Select(name => Path.Combine(directoryPath, name))
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (files.Length == 0) return false;

        int mappedCount = 0;
        for (int i = 0; i < files.Length; i++)
        {
            string path = files[i];
            int before = _commands.Count;
            if (try_load_command_dump_file(path, warnLog))
            {
                mappedCount += Math.Max(0, _commands.Count - before);
            }
        }

        if (mappedCount > 0)
        {
            infoLog?.Invoke($"Loaded {mappedCount} command entries from parser text output.");
        }

        return mappedCount > 0;
    }

    private bool try_load_command_dump_file(string path, Action<string>? warnLog)
    {
        int mapped = 0;
        try
        {
            foreach (string line in File.ReadLines(path))
            {
                Match match = _commandDumpRegex.Match(line);
                if (!match.Success) continue;

                string idText = match.Groups["id"].Value;
                if (!ushort.TryParse(idText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort id) || id == 0)
                {
                    continue;
                }

                string payload = (match.Groups["payload"].Value ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(payload)) continue;

                int braceIndex = payload.IndexOf('{');
                string name = (braceIndex >= 0 ? payload[..braceIndex] : payload).Trim();
                if (string.IsNullOrWhiteSpace(name)) continue;

                string description = string.Empty;
                int closingBrace = find_metadata_closing_brace(payload);
                if (closingBrace >= 0 && closingBrace + 1 < payload.Length)
                {
                    description = payload[(closingBrace + 1)..].Trim();
                }

                CommandLikeRecord record = get_or_create(_commands, id);
                bool changed = false;
                if (string.IsNullOrWhiteSpace(record.Name))
                {
                    record.Name = name;
                    changed = true;
                }

                if (!string.IsNullOrWhiteSpace(description) && string.IsNullOrWhiteSpace(record.Description))
                {
                    record.Description = description;
                    changed = true;
                }

                if (changed) mapped++;
            }
        }
        catch (Exception ex)
        {
            warnLog?.Invoke($"Failed reading command dump {path}: {ex.Message}");
            return false;
        }

        return mapped > 0;
    }

    private static int find_metadata_closing_brace(string payload)
    {
        int start = payload.IndexOf('{');
        if (start < 0) return -1;

        int depth = 0;
        for (int i = start; i < payload.Length; i++)
        {
            char c = payload[i];
            if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
            {
                depth--;
                if (depth == 0) return i;
            }
        }

        return -1;
    }

    private bool try_load_compact_json(string directoryPath, Action<string>? infoLog, Action<string>? warnLog)
    {
        int mappedCount = 0;
        foreach (string fileName in enumerate_compact_json_candidates())
        {
            string path = Path.Combine(directoryPath, fileName);
            if (!File.Exists(path)) continue;
            mappedCount += try_load_compact_json_file(path, warnLog);
        }

        if (mappedCount > 0)
        {
            infoLog?.Invoke($"Loaded {mappedCount} compact mapping entries from JSON.");
        }

        return mappedCount > 0;
    }

    private IEnumerable<string> enumerate_compact_json_candidates()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string locale = normalize_locale(_preferredLocale);

        string localized = $"ffx-mappings.{locale}.json";
        if (seen.Add(localized)) yield return localized;

        if (!locale.Equals("us", StringComparison.OrdinalIgnoreCase))
        {
            string us = "ffx-mappings.us.json";
            if (seen.Add(us)) yield return us;
        }

        for (int i = 0; i < _legacyCompactJsonNames.Length; i++)
        {
            if (seen.Add(_legacyCompactJsonNames[i])) yield return _legacyCompactJsonNames[i];
        }
    }

    private int try_load_compact_json_file(string path, Action<string>? warnLog)
    {
        int mapped = 0;
        try
        {
            using FileStream stream = File.OpenRead(path);
            using JsonDocument document = JsonDocument.Parse(stream);

            JsonElement root = document.RootElement;

            // Surface schema drift early. Loader keeps going either way — see
            // ExpectedBundleSchemaVersion docs above for the load-defensive contract.
            if (root.TryGetProperty("SchemaVersion", out JsonElement schemaVersion)
                && schemaVersion.ValueKind == JsonValueKind.Number
                && schemaVersion.TryGetInt32(out int actualVersion)
                && actualVersion != ExpectedBundleSchemaVersion)
            {
                warnLog?.Invoke(
                    $"Runtime bundle schema mismatch in {Path.GetFileName(path)}: " +
                    $"expected SchemaVersion={ExpectedBundleSchemaVersion}, got {actualVersion}. " +
                    "Continuing with best-effort field reads.");
            }

            if (root.TryGetProperty("Domains", out JsonElement domains) && domains.ValueKind == JsonValueKind.Object)
            {
                mapped += merge_runtime_bundle_domains(domains);
                return mapped;
            }

            if (root.TryGetProperty("domain", out JsonElement domainNode) && domainNode.ValueKind == JsonValueKind.String
                && root.TryGetProperty("entries", out JsonElement entriesNode) && entriesNode.ValueKind == JsonValueKind.Object)
            {
                mapped += merge_single_domain_file(domainNode.GetString() ?? string.Empty, entriesNode);
                return mapped;
            }

            if (!root.TryGetProperty("commands", out JsonElement commands) || commands.ValueKind != JsonValueKind.Array)
            {
                return mapped;
            }

            foreach (JsonElement entry in commands.EnumerateArray())
            {
                if (!try_parse_compact_command_entry(entry, out ushort id, out string name, out string kind)) continue;

                Dictionary<ushort, CommandLikeRecord> destination = select_command_destination(id, kind);
                CommandLikeRecord record = get_or_create(destination, id);
                bool changed = false;
                if (string.IsNullOrWhiteSpace(record.Name) && !string.IsNullOrWhiteSpace(name))
                {
                    record.Name = name;
                    changed = true;
                }

                if (changed)
                {
                    mapped++;
                }
            }
        }
        catch (Exception ex)
        {
            warnLog?.Invoke($"Failed reading compact mappings {path}: {ex.Message}");
            return 0;
        }

        return mapped;
    }

    private int merge_runtime_bundle_domains(JsonElement domains)
    {
        int mapped = 0;
        if (domains.TryGetProperty("Commands", out JsonElement commands) && commands.ValueKind == JsonValueKind.Object)
        {
            mapped += merge_command_domain_object(commands, _commands);
        }
        if (domains.TryGetProperty("AutoAbilities", out JsonElement autoAbilities) && autoAbilities.ValueKind == JsonValueKind.Object)
        {
            mapped += merge_command_domain_object(autoAbilities, _autoAbilities);
        }
        if (domains.TryGetProperty("KeyItems", out JsonElement keyItems) && keyItems.ValueKind == JsonValueKind.Object)
        {
            mapped += merge_command_domain_object(keyItems, _keyItems);
        }
        if (domains.TryGetProperty("Monsters", out JsonElement monsters) && monsters.ValueKind == JsonValueKind.Object)
        {
            mapped += merge_monster_domain_object(monsters);
        }
        if (domains.TryGetProperty("Battles", out JsonElement battles) && battles.ValueKind == JsonValueKind.Object)
        {
            mapped += merge_script_domain_object(battles, _battles);
        }
        if (domains.TryGetProperty("Events", out JsonElement eventsNode) && eventsNode.ValueKind == JsonValueKind.Object)
        {
            mapped += merge_script_domain_object(eventsNode, _events);
        }

        return mapped;
    }

    private int merge_single_domain_file(string domain, JsonElement entries)
    {
        string normalized = (domain ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "commands" => merge_command_domain_object(entries, _commands),
            "autoabilities" => merge_command_domain_object(entries, _autoAbilities),
            "keyitems" => merge_command_domain_object(entries, _keyItems),
            "monsters" => merge_monster_domain_object(entries),
            "battles" => merge_script_domain_object(entries, _battles),
            "events" => merge_script_domain_object(entries, _events),
            _ => 0
        };
    }

    private static int merge_command_domain_object(JsonElement entries, Dictionary<ushort, CommandLikeRecord> destination)
    {
        int mapped = 0;
        foreach (JsonProperty prop in entries.EnumerateObject())
        {
            if (!try_parse_compact_id(prop.Name, out ushort id) || id == 0) continue;
            if (prop.Value.ValueKind != JsonValueKind.Object) continue;

            string name = string.Empty;
            if (prop.Value.TryGetProperty("Name", out JsonElement nameNode) && nameNode.ValueKind == JsonValueKind.String)
            {
                name = nameNode.GetString() ?? string.Empty;
            }

            string description = string.Empty;
            if (prop.Value.TryGetProperty("Description", out JsonElement descNode) && descNode.ValueKind == JsonValueKind.String)
            {
                description = descNode.GetString() ?? string.Empty;
            }

            string damageType = string.Empty;
            if (prop.Value.TryGetProperty("DamageType", out JsonElement damageTypeNode) && damageTypeNode.ValueKind == JsonValueKind.String)
            {
                damageType = damageTypeNode.GetString() ?? string.Empty;
            }

            JsonElement? mechanics = null;
            if (prop.Value.TryGetProperty("Mechanics", out JsonElement mechNode) && mechNode.ValueKind == JsonValueKind.Object)
            {
                mechanics = mechNode.Clone();
            }

            if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(description) && string.IsNullOrWhiteSpace(damageType) && mechanics is null)
            {
                continue;
            }

            CommandLikeRecord record = get_or_create(destination, id);
            bool changed = false;
            if (!string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(record.Name))
            {
                record.Name = name;
                changed = true;
            }
            if (!string.IsNullOrWhiteSpace(description) && string.IsNullOrWhiteSpace(record.Description))
            {
                record.Description = description;
                changed = true;
            }
            if (!string.IsNullOrWhiteSpace(damageType) && string.IsNullOrWhiteSpace(record.DamageType))
            {
                record.DamageType = damageType;
                changed = true;
            }
            if (mechanics is not null && !record.Mechanics.HasValue)
            {
                record.Mechanics = mechanics;

                // Promote hit_count to a typed field once, at load. 0 = null /
                // non-numeric (the ~150 non-damaging records).
                if (mechNode.TryGetProperty("hit_count", out JsonElement hcNode)
                    && hcNode.ValueKind == JsonValueKind.Number
                    && hcNode.TryGetInt32(out int hc))
                {
                    record.HitCount = hc;
                }

                changed = true;
            }
            if (changed) mapped++;
        }

        return mapped;
    }

    private int merge_monster_domain_object(JsonElement entries)
    {
        int mapped = 0;
        foreach (JsonProperty prop in entries.EnumerateObject())
        {
            if (!int.TryParse(prop.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) || id < 0) continue;
            if (prop.Value.ValueKind != JsonValueKind.Object) continue;

            MonsterRecord record = get_or_create(_monsters, id);
            bool changed = false;

            if (prop.Value.TryGetProperty("Name", out JsonElement nameNode) && nameNode.ValueKind == JsonValueKind.String)
            {
                string name = nameNode.GetString() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(record.Name))
                {
                    record.Name = name;
                    changed = true;
                }
            }

            if (prop.Value.TryGetProperty("Sensor", out JsonElement sensorNode) && sensorNode.ValueKind == JsonValueKind.String)
            {
                string sensor = sensorNode.GetString() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(sensor) && string.IsNullOrWhiteSpace(record.Sensor))
                {
                    record.Sensor = sensor;
                    changed = true;
                }
            }

            if (prop.Value.TryGetProperty("Scan", out JsonElement scanNode) && scanNode.ValueKind == JsonValueKind.String)
            {
                string scan = scanNode.GetString() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(scan) && string.IsNullOrWhiteSpace(record.Scan))
                {
                    record.Scan = scan;
                    changed = true;
                }
            }

            if (changed) mapped++;
        }

        return mapped;
    }

    private static int merge_script_domain_object(JsonElement entries, Dictionary<string, SortedDictionary<int, string>> destination)
    {
        int mapped = 0;
        foreach (JsonProperty prop in entries.EnumerateObject())
        {
            string id = normalize_script_id(prop.Name);
            if (string.IsNullOrWhiteSpace(id)) continue;

            if (prop.Value.ValueKind == JsonValueKind.String)
            {
                string text = prop.Value.GetString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(text)) continue;
                if (!destination.TryGetValue(id, out SortedDictionary<int, string>? lines))
                {
                    lines = new SortedDictionary<int, string>();
                    destination[id] = lines;
                }
                if (!lines.ContainsKey(0))
                {
                    lines[0] = text;
                    mapped++;
                }
                continue;
            }

            if (prop.Value.ValueKind != JsonValueKind.Object) continue;
            if (!destination.TryGetValue(id, out SortedDictionary<int, string>? map))
            {
                map = new SortedDictionary<int, string>();
                destination[id] = map;
            }

            foreach (JsonProperty line in prop.Value.EnumerateObject())
            {
                if (!int.TryParse(line.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx)) continue;
                if (line.Value.ValueKind != JsonValueKind.String) continue;
                string text = line.Value.GetString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(text)) continue;
                if (!map.ContainsKey(idx))
                {
                    map[idx] = text;
                    mapped++;
                }
            }
        }

        return mapped;
    }

    private static bool try_parse_compact_command_entry(JsonElement entry, out ushort id, out string name, out string kind)
    {
        id = 0;
        name = string.Empty;
        kind = string.Empty;

        if (!entry.TryGetProperty("id", out JsonElement idNode)) return false;
        if (!try_parse_compact_id(idNode, out id)) return false;
        if (id == 0) return false;

        if (!entry.TryGetProperty("name", out JsonElement nameNode) || nameNode.ValueKind != JsonValueKind.String) return false;
        name = nameNode.GetString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name)) return false;

        if (entry.TryGetProperty("kind", out JsonElement kindNode) && kindNode.ValueKind == JsonValueKind.String)
        {
            kind = kindNode.GetString() ?? string.Empty;
        }

        return true;
    }

    private static bool try_parse_compact_id(JsonElement node, out ushort id)
    {
        id = 0;
        if (node.ValueKind == JsonValueKind.Number)
        {
            return node.TryGetUInt16(out id);
        }

        if (node.ValueKind != JsonValueKind.String) return false;
        string text = (node.GetString() ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            text = text[2..];
        }

        if (ushort.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out id)) return true;
        return ushort.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out id);
    }

    private static bool try_parse_compact_id(string text, out ushort id)
    {
        id = 0;
        string value = (text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            value = value[2..];
        }

        if (ushort.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out id)) return true;
        return ushort.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out id);
    }

    private Dictionary<ushort, CommandLikeRecord> select_command_destination(ushort id, string kind)
    {
        string normalized = (kind ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized.Contains("autoability", StringComparison.Ordinal)) return _autoAbilities;
        if (normalized.Contains("keyitem", StringComparison.Ordinal)) return _keyItems;
        if (id >= 0x8000 && id <= 0x8FFF) return _autoAbilities;
        if (id >= 0xA000 && id <= 0xAFFF) return _keyItems;
        return _commands;
    }

    private string pick_localized_text(string[] row, Dictionary<string, int> header)
    {
        foreach (string locale in locale_priority(_preferredLocale))
        {
            int idx = get_index(header, locale);
            if (idx < 0) continue;
            string value = read_cell(row, idx);
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }

        string[] fallbackLocales = ["us", "de", "fr", "sp", "it", "jp", "ch", "kr"];
        for (int i = 0; i < fallbackLocales.Length; i++)
        {
            int idx = get_index(header, fallbackLocales[i]);
            if (idx < 0) continue;
            string value = read_cell(row, idx);
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }

        return string.Empty;
    }

    private static IEnumerable<string> locale_priority(string preferred)
    {
        return preferred switch
        {
            "de" => ["de", "us"],
            "fr" => ["fr", "us"],
            "it" => ["it", "us"],
            "sp" => ["sp", "us"],
            "jp" => ["jp", "us"],
            "ch" => ["ch", "us"],
            "kr" => ["kr", "us"],
            _ => ["us", "de"]
        };
    }

    private static List<string[]> read_csv(string path, Action<string>? warnLog)
    {
        var rows = new List<string[]>();
        try
        {
            foreach (string line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                rows.Add(parse_csv_line(line));
            }
        }
        catch (Exception ex)
        {
            warnLog?.Invoke($"Failed reading {path}: {ex.Message}");
            return [];
        }

        return rows;
    }

    private static string[] parse_csv_line(string line)
    {
        var cells = new List<string>();
        var sb = new StringBuilder(line.Length);
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    sb.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }

                continue;
            }

            if (c == ',' && !inQuotes)
            {
                cells.Add(sb.ToString());
                sb.Clear();
                continue;
            }

            sb.Append(c);
        }

        cells.Add(sb.ToString());
        return cells.Select(x => x.Trim()).ToArray();
    }

    private static Dictionary<string, int> map_header(string[] headerRow)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < headerRow.Length; i++)
        {
            string key = normalize_header_key(headerRow[i]);
            if (!string.IsNullOrWhiteSpace(key) && !map.ContainsKey(key))
            {
                map[key] = i;
            }
        }

        return map;
    }

    private static int get_index(Dictionary<string, int> header, params string[] keys)
    {
        for (int i = 0; i < keys.Length; i++)
        {
            string key = normalize_header_key(keys[i]);
            if (header.TryGetValue(key, out int idx)) return idx;
        }

        return -1;
    }

    private static string normalize_header_key(string raw) =>
        (raw ?? string.Empty)
        .Trim()
        .ToLowerInvariant()
        .Replace(" ", string.Empty, StringComparison.Ordinal)
        .Replace("_", string.Empty, StringComparison.Ordinal);

    private static string read_cell(string[] row, int index)
    {
        if (index < 0 || index >= row.Length) return string.Empty;
        return row[index] ?? string.Empty;
    }

    private static bool try_parse_hex(string raw, out ushort value)
    {
        value = 0;
        string text = (raw ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            text = text[2..];
        }

        return ushort.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }

    private static string normalize_script_id(string id) => (id ?? string.Empty).Trim().ToLowerInvariant();

    private static string normalize_locale(string locale)
    {
        string lower = (locale ?? string.Empty).Trim().ToLowerInvariant();
        if (lower.StartsWith("de", StringComparison.Ordinal)) return "de";
        if (lower.StartsWith("fr", StringComparison.Ordinal)) return "fr";
        if (lower.StartsWith("it", StringComparison.Ordinal)) return "it";
        if (lower.StartsWith("es", StringComparison.Ordinal) || lower.StartsWith("sp", StringComparison.Ordinal)) return "sp";
        if (lower.StartsWith("ja", StringComparison.Ordinal) || lower.StartsWith("jp", StringComparison.Ordinal)) return "jp";
        if (lower.StartsWith("ko", StringComparison.Ordinal) || lower.StartsWith("kr", StringComparison.Ordinal)) return "kr";
        if (lower.StartsWith("zh", StringComparison.Ordinal) || lower.StartsWith("ch", StringComparison.Ordinal)) return "ch";
        return "us";
    }

    private static string normalize_path(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static TValue get_or_create<TKey, TValue>(Dictionary<TKey, TValue> map, TKey key) where TKey : notnull where TValue : class, new()
    {
        if (!map.TryGetValue(key, out TValue? value))
        {
            value = new TValue();
            map[key] = value;
        }

        return value;
    }

    private void clear()
    {
        _commands.Clear();
        _autoAbilities.Clear();
        _keyItems.Clear();
        _monsters.Clear();
        _events.Clear();
        _battles.Clear();
        _sources.Clear();
        rebuild_symbolic_command_map();
    }

    private void rebuild_symbolic_command_map()
    {
        _symbolicCommands.Clear();
        add_symbolic_constants(typeof(FhFfx.Ids.PlayerCommandId), "PCOM_");
        add_symbolic_constants(typeof(FhFfx.Ids.MonsterCommandId), "MCOM_");
        add_symbolic_constants(typeof(FhFfx.Ids.BossCommandId), "MCOM2_");
        add_symbolic_constants(typeof(FhFfx.Ids.ItemId), "ITEM_");
    }

    private void add_symbolic_constants(Type type, string prefixToTrim)
    {
        FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.Static);
        for (int i = 0; i < fields.Length; i++)
        {
            FieldInfo field = fields[i];
            if (field.FieldType != typeof(ushort)) continue;
            if (!field.IsLiteral && !field.IsStatic) continue;

            object? valueObj = field.GetRawConstantValue();
            if (valueObj is not ushort id || id == 0) continue;
            if (_symbolicCommands.ContainsKey(id)) continue;

            string symbol = field.Name;
            if (!string.IsNullOrWhiteSpace(prefixToTrim) && symbol.StartsWith(prefixToTrim, StringComparison.OrdinalIgnoreCase))
            {
                symbol = symbol[prefixToTrim.Length..];
            }

            _symbolicCommands[id] = humanize_symbol(symbol);
        }
    }

    private static string humanize_symbol(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return string.Empty;
        string normalized = symbol.Replace('_', ' ');
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(normalized.ToLowerInvariant());
    }
}
