using System.Text.RegularExpressions;
using SHARD.Core.Enums;

namespace SHARD.Core.Schema;

/// <summary>
/// Extracts column structure from a sqlite_master "CREATE TABLE" statement.
/// Scoped to the subset of SQL needed to describe storage layout (columns, declared
/// types, PRIMARY KEY / NOT NULL / UNIQUE) — not a general SQL parser.
/// </summary>
public static class CreateTableParser
{
    private static readonly string[] ColumnConstraintKeywords =
        { "NOT", "PRIMARY", "UNIQUE", "CHECK", "DEFAULT", "COLLATE", "REFERENCES", "GENERATED", "AS" };

    private static readonly string[] TableConstraintLeaders =
        { "PRIMARY", "UNIQUE", "CHECK", "FOREIGN", "CONSTRAINT" };

    public static TableSchema? ExtractTableSchema(string sql)
    {
        string originalSql = sql;
        sql = StripLineComments(sql);
        int openParen = FindTableBodyStart(sql);
        if (openParen < 0) return null;

        int closeParen = FindMatchingParen(sql, openParen);
        if (closeParen < 0) return null;

        var schema = new TableSchema { TableName = ExtractTableName(sql, openParen), Sql = originalSql };
        string body = sql.Substring(openParen + 1, closeParen - openParen - 1);
        var tablePrimaryKeyColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string rawPart in SplitTopLevel(body, ','))
        {
            string part = rawPart.Trim();
            if (part.Length == 0) continue;

            string leadingWord = ReadIdentifierOrKeyword(part, 0, out _).ToUpperInvariant();
            if (Array.IndexOf(TableConstraintLeaders, leadingWord) >= 0)
            {
                if (leadingWord == "PRIMARY")
                    tablePrimaryKeyColumns.UnionWith(ExtractParenColumnNames(part));
                continue;
            }

            var column = ParseColumnDefinition(part);
            if (column != null) schema.Columns.Add(column);
        }

        foreach (var column in schema.Columns)
        {
            if (tablePrimaryKeyColumns.Contains(column.Name)) column.IsPrimaryKey = true;
        }

        bool withoutRowid = Regex.IsMatch(sql.Substring(closeParen + 1), @"\bWITHOUT\s+ROWID\b", RegexOptions.IgnoreCase);
        var primaryKeyColumns = schema.Columns.Where(c => c.IsPrimaryKey).ToList();
        if (!withoutRowid && primaryKeyColumns.Count == 1 &&
            string.Equals(primaryKeyColumns[0].DeclaredType, "INTEGER", StringComparison.OrdinalIgnoreCase))
        {
            primaryKeyColumns[0].IsRowIdAlias = true;
        }

        return schema;
    }

    public static TypeAffinity ResolveAffinity(string? declaredType)
    {
        if (string.IsNullOrWhiteSpace(declaredType)) return TypeAffinity.Blob;
        string t = declaredType.ToUpperInvariant();
        if (t.Contains("INT")) return TypeAffinity.Integer;
        if (t.Contains("CHAR") || t.Contains("CLOB") || t.Contains("TEXT")) return TypeAffinity.Text;
        if (t.Contains("BLOB")) return TypeAffinity.Blob;
        if (t.Contains("REAL") || t.Contains("FLOA") || t.Contains("DOUB")) return TypeAffinity.Real;
        return TypeAffinity.Numeric;
    }

    private static ColumnDefinition? ParseColumnDefinition(string text)
    {
        string name = ReadIdentifierOrKeyword(text, 0, out int afterName);
        if (name.Length == 0) return null;
        string rest = text.Substring(afterName).Trim();

        int keywordIdx = FindTopLevelKeyword(rest, ColumnConstraintKeywords);
        string declaredType = (keywordIdx < 0 ? rest : rest.Substring(0, keywordIdx)).Trim();
        string constraints = keywordIdx < 0 ? "" : rest.Substring(keywordIdx);

        bool notNull = false;
        int notIdx = IndexOfWord(constraints, "NOT", 0);
        if (notIdx >= 0 && IndexOfWord(constraints, "NULL", notIdx) >= 0) notNull = true;

        return new ColumnDefinition
        {
            Name = name,
            DeclaredType = declaredType.Length == 0 ? null : declaredType,
            Affinity = ResolveAffinity(declaredType),
            IsPrimaryKey = IndexOfWord(constraints, "PRIMARY", 0) >= 0,
            IsNotNull = notNull,
            IsUnique = IndexOfWord(constraints, "UNIQUE", 0) >= 0,
        };
    }

    private static List<string> ExtractParenColumnNames(string constraintText)
    {
        int open = constraintText.IndexOf('(');
        if (open < 0) return new();
        int close = FindMatchingParen(constraintText, open);
        if (close < 0) return new();

        string inner = constraintText.Substring(open + 1, close - open - 1);
        var names = new List<string>();
        foreach (string part in SplitTopLevel(inner, ','))
        {
            string name = ReadIdentifierOrKeyword(part.Trim(), 0, out _);
            if (name.Length > 0) names.Add(name);
        }
        return names;
    }

    private static int FindTableBodyStart(string sql)
    {
        int tableIdx = IndexOfWord(sql, "TABLE", 0);
        if (tableIdx < 0) return -1;

        int i = tableIdx + "TABLE".Length;
        while (i < sql.Length)
        {
            char c = sql[i];
            if (c == '(') return i;
            if (c is '\'' or '"' or '`' or '[') { i = SkipQuotedOrBracketed(sql, i); continue; }
            i++;
        }
        return -1;
    }

    private static string ExtractTableName(string sql, int openParen)
    {
        int tableIdx = IndexOfWord(sql, "TABLE", 0);
        string between = sql.Substring(tableIdx + "TABLE".Length, openParen - tableIdx - "TABLE".Length).Trim();

        int existsIdx = IndexOfWord(between, "EXISTS", 0);
        if (existsIdx >= 0) between = between.Substring(existsIdx + "EXISTS".Length).Trim();

        int dot = between.LastIndexOf('.');
        if (dot >= 0) between = between.Substring(dot + 1).Trim();

        return Unquote(between);
    }

    private static int FindMatchingParen(string s, int openIdx)
    {
        int depth = 0;
        for (int i = openIdx; i < s.Length; i++)
        {
            char c = s[i];
            if (c is '\'' or '"' or '`' or '[') { i = SkipQuotedOrBracketed(s, i) - 1; continue; }
            if (c == '(') depth++;
            else if (c == ')')
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        return -1;
    }

    private static List<string> SplitTopLevel(string s, char delimiter)
    {
        var parts = new List<string>();
        int depth = 0, start = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c is '\'' or '"' or '`' or '[') { i = SkipQuotedOrBracketed(s, i) - 1; continue; }
            if (c == '(') depth++;
            else if (c == ')') depth--;
            else if (c == delimiter && depth == 0)
            {
                parts.Add(s.Substring(start, i - start));
                start = i + 1;
            }
        }
        parts.Add(s.Substring(start));
        return parts;
    }

    private static int FindTopLevelKeyword(string s, string[] keywords)
    {
        int depth = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c is '\'' or '"' or '`' or '[') { i = SkipQuotedOrBracketed(s, i) - 1; continue; }
            if (c == '(') { depth++; continue; }
            if (c == ')') { depth--; continue; }
            if (depth != 0 || !char.IsLetter(c)) continue;
            if (i > 0 && (char.IsLetterOrDigit(s[i - 1]) || s[i - 1] == '_')) continue;

            foreach (string kw in keywords)
            {
                if (i + kw.Length <= s.Length &&
                    string.Compare(s, i, kw, 0, kw.Length, StringComparison.OrdinalIgnoreCase) == 0 &&
                    (i + kw.Length == s.Length || !(char.IsLetterOrDigit(s[i + kw.Length]) || s[i + kw.Length] == '_')))
                {
                    return i;
                }
            }
        }
        return -1;
    }

    private static int IndexOfWord(string s, string word, int start)
    {
        int idx = start;
        while (true)
        {
            idx = s.IndexOf(word, idx, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return -1;

            bool leftOk = idx == 0 || !(char.IsLetterOrDigit(s[idx - 1]) || s[idx - 1] == '_');
            int rightIdx = idx + word.Length;
            bool rightOk = rightIdx >= s.Length || !(char.IsLetterOrDigit(s[rightIdx]) || s[rightIdx] == '_');
            if (leftOk && rightOk) return idx;

            idx += word.Length;
        }
    }

    private static string ReadIdentifierOrKeyword(string s, int pos, out int endPos)
    {
        while (pos < s.Length && char.IsWhiteSpace(s[pos])) pos++;
        if (pos >= s.Length) { endPos = pos; return ""; }

        if (s[pos] is '"' or '`' or '[' or '\'')
        {
            int end = SkipQuotedOrBracketed(s, pos);
            endPos = end;
            return Unquote(s.Substring(pos, end - pos));
        }

        int start = pos;
        while (pos < s.Length && (char.IsLetterOrDigit(s[pos]) || s[pos] == '_')) pos++;
        endPos = pos;
        return s.Substring(start, pos - start);
    }

    private static string Unquote(string s)
    {
        s = s.Trim();
        if (s.Length >= 2)
        {
            char first = s[0], last = s[^1];
            if ((first == '"' && last == '"') || (first == '`' && last == '`') ||
                (first == '\'' && last == '\'') || (first == '[' && last == ']'))
            {
                return s.Substring(1, s.Length - 2);
            }
        }
        return s;
    }

    private static string StripLineComments(string sql)
    {
        var sb = new System.Text.StringBuilder(sql.Length);
        int i = 0;
        while (i < sql.Length)
        {
            // Skip quoted/bracketed identifiers and string literals verbatim
            if (sql[i] is '\'' or '"' or '`' or '[')
            {
                int end = SkipQuotedOrBracketed(sql, i);
                sb.Append(sql, i, end - i);
                i = end;
                continue;
            }

            // Line comment — skip to end of line
            if (i + 1 < sql.Length && sql[i] == '-' && sql[i + 1] == '-')
            {
                while (i < sql.Length && sql[i] != '\n') i++;
                continue;
            }

            sb.Append(sql[i]);
            i++;
        }
        return sb.ToString();
    }

    private static int SkipQuotedOrBracketed(string s, int i)
    {
        char open = s[i];
        char close = open == '[' ? ']' : open;
        int j = i + 1;
        while (j < s.Length)
        {
            if (s[j] == close)
            {
                if (close != ']' && j + 1 < s.Length && s[j + 1] == close) { j += 2; continue; }
                return j + 1;
            }
            j++;
        }
        return s.Length;
    }
}
