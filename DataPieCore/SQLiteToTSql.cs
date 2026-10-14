using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
namespace DataPieCore;

// Explicit supported-subset translation; unknown SQLite constructs are rejected, never guessed.
internal static class SQLiteToTSql
{
    private static readonly Regex Lexer=new(@"--[^\r\n]*|/\*[\s\S]*?\*/|'(?:''|[^'])*'|\[(?:\]\]|[^\]])*\]|""(?:""""|[^""])*""|`(?:``|[^`])*`|[\p{L}_@][\p{L}\p{N}_$@]*|\d+(?:\.\d+)?|\|\||<>|!=|<=|>=|\S");
    private static string Q(string name)=>"["+name.Replace("]","]]")+"]";
    private static string Name(string token)=>token.StartsWith("[") ? token.Substring(1,token.Length-2).Replace("]]","]") : token.StartsWith("\"") ? token.Substring(1,token.Length-2).Replace("\"\"","\"") : token.StartsWith("`") ? token.Substring(1,token.Length-2).Replace("``","`") : token;
    private static string QuoteToken(string token) => token.StartsWith("\"") || token.StartsWith("`") ? Q(Name(token)) : token.StartsWith("'") ? "N"+token : token;
    private static List<string> Tokens(string sql)=>Lexer.Matches(sql).Cast<Match>().Select(m=>m.Value).Where(t=>!t.StartsWith("--")&&!t.StartsWith("/*")).ToList();
    private static string Unwrap(string text)
    {
        var tokens=Tokens(text);
        while(tokens.Count>1 && tokens[0]=="(" && tokens[^1]==")")
        {
            int d=0; bool whole=true;
            for(int i=0;i<tokens.Count-1;i++) { if(tokens[i]=="(")d++; else if(tokens[i]==")")d--; if(d==0){whole=false;break;} }
            if(!whole)break; tokens.RemoveAt(tokens.Count-1);tokens.RemoveAt(0);
        }
        return string.Join(" ",tokens);
    }
    internal static string Expression(string sql)=>Rewrite(Tokens(sql));
    private static string Rewrite(List<string> tokens)
    {
        var result=new List<string>();
        var constructs=new HashSet<string>(StringComparer.OrdinalIgnoreCase) {"IN","EXISTS","OVER","VALUES","AS","SELECT","FROM","WHERE","AND","OR","NOT"};
        var common=new HashSet<string>(StringComparer.OrdinalIgnoreCase) {"COALESCE","NULLIF","ABS","UPPER","LOWER","REPLACE","ROUND","COUNT","SUM","AVG","MIN","MAX","ROW_NUMBER","RANK","DENSE_RANK","LAG","LEAD"};
        for(int i=0;i<tokens.Count;i++)
        {
            var token=tokens[i]; string upper=token.ToUpperInvariant();
            if(token=="||") throw new NotSupportedException("SQLite concatenation requires explicit NULL-semantics review.");
            if(token is "'" or "\"" or "`") throw new NotSupportedException("Unclosed SQL literal or identifier.");
            if(i+1>=tokens.Count || tokens[i+1]!="(" || token.StartsWith("[") || token.StartsWith("\"") || token.StartsWith("`") || token.StartsWith("'") || !Regex.IsMatch(token,@"^[\p{L}_][\p{L}\p{N}_]*$") || constructs.Contains(token) || i>0&&tokens[i-1].Equals("INTO",StringComparison.OrdinalIgnoreCase))
            {result.Add(QuoteToken(token));continue;}
            int end=i+2,depth=1;var args=new List<List<string>> {new()};
            for(;end<tokens.Count;end++)
            {
                if(tokens[end]=="(")depth++;
                if(tokens[end]==")" && --depth==0) break;
                if(tokens[end]=="," && depth==1)args.Add(new()); else args[^1].Add(tokens[end]);
            }
            if(end==tokens.Count)throw new NotSupportedException("Unbalanced function parentheses.");
            var a=args.Select(Rewrite).ToArray();string expression;
            if(upper=="CAST")
            {
                var raw=args[0];int nest=0,at=-1;
                for(int j=0;j<raw.Count;j++){if(raw[j]=="(")nest++;if(raw[j]==")")nest--;if(nest==0&&raw[j].Equals("AS",StringComparison.OrdinalIgnoreCase))at=j;}
                if(at<0)throw new NotSupportedException("Invalid CAST.");
                string type=string.Join(" ",raw.Skip(at+1)).ToUpperInvariant();
                type=type switch {"INTEGER" or "INT"=>"BIGINT","REAL"=>"FLOAT","TEXT"=>"NVARCHAR(MAX)","NUMERIC"=>"DECIMAL(38,10)","FLOAT" or "BIGINT"=>type,_=>throw new NotSupportedException("Unsupported SQLite CAST: "+type)};
                expression="CAST("+Rewrite(raw.Take(at).ToList())+" AS "+type+")";
            }
            else if(upper=="IFNULL" && a.Length==2) expression="ISNULL("+string.Join(", ",a)+")";
            else if((upper=="RTRIM" || upper=="LTRIM" || upper=="TRIM") && (a.Length==1 || a.Length==2&&Unwrap(args[1].SingleOrDefault()??"")=="' '")) expression=upper+"("+a[0]+")";
            else if(upper=="LENGTH" && a.Length==1) expression="(CASE WHEN "+a[0]+" IS NULL THEN NULL ELSE LEN(CONCAT(CAST("+a[0]+" AS NVARCHAR(MAX)), N'#')) - 1 END)";
            else if((upper=="SUBSTR" || upper=="SUBSTRING") && (a.Length==2 || a.Length==3))
            {
                string start=Unwrap(string.Join(" ",args[1]));
                if(!int.TryParse(start,NumberStyles.None,CultureInfo.InvariantCulture,out int n)||n<1)throw new NotSupportedException("SUBSTR with dynamic/nonpositive start requires manual conversion.");
                if(a.Length==3 && (!int.TryParse(Unwrap(string.Join(" ",args[2])),out int length)||length<0))throw new NotSupportedException("SUBSTR with dynamic/negative length requires manual conversion.");
                expression="SUBSTRING("+a[0]+", "+n+", "+(a.Length==3?a[2]:"2147483647")+")";
            }
            else if(upper=="STRFTIME" && a.Length==2)
            {
                string format=string.Join("",args[0]);string date="TRY_CONVERT(DATETIME2, "+a[1]+")";
                expression=format switch
                {
                    "'%Y'"=>"CONVERT(NVARCHAR(4), DATEPART(YEAR, "+date+"))",
                    "'%m'"=>"RIGHT(N'0' + CONVERT(NVARCHAR(2), DATEPART(MONTH, "+date+")), 2)",
                    "'%d'"=>"RIGHT(N'0' + CONVERT(NVARCHAR(2), DATEPART(DAY, "+date+")), 2)",
                    "'%Y-%m-%d'"=>"CONVERT(NVARCHAR(10), "+date+", 23)",
                    _=>throw new NotSupportedException("Unsupported strftime format: "+format)
                };
            }
            else if((upper=="MAX" || upper=="MIN") && a.Length>1)
                expression="(CASE WHEN "+string.Join(" OR ",a.Select(x=>x+" IS NULL"))+" THEN NULL ELSE (SELECT "+upper+"(v) FROM (VALUES "+string.Join(", ",a.Select(x=>"("+x+")"))+") AS _values(v)) END)";
            else if(common.Contains(upper))expression=upper+"("+string.Join(", ",a)+")";
            else throw new NotSupportedException("Unsupported SQLite function: "+token);
            result.Add(expression);i=end;
        }
        return string.Join(" ",result);
    }
    internal static string Body(string sql,IReadOnlyCollection<string> parameters)
    {
        var tokens=Tokens(sql);
        var forbidden=new HashSet<string>(StringComparer.OrdinalIgnoreCase) {"PRAGMA","ATTACH","DETACH","VACUUM","RETURNING","LIMIT","CONFLICT","RAISE","CREATE","ALTER","DROP","BEGIN","COMMIT","ROLLBACK","GO","GLOB","MATCH","REGEXP","COLLATE","ISNULL","NOTNULL"};
        if(tokens.Any(t=>forbidden.Contains(t)))throw new NotSupportedException("SQLite statement options, DDL or transaction commands require manual conversion.");
        foreach(var token in tokens.Where(t=>t.StartsWith("@")))if(!parameters.Contains(token,StringComparer.OrdinalIgnoreCase))throw new NotSupportedException("Parameter metadata missing for "+token);
        bool beginning=true;
        string statement="";
        for(int i=0;i<tokens.Count;i++)
        {
            if(tokens[i]==";"){beginning=true;statement="";continue;}
            if(beginning && !new[]{"SELECT","UPDATE","DELETE","INSERT","WITH"}.Contains(tokens[i].ToUpperInvariant()))throw new NotSupportedException("Only SELECT, INSERT, UPDATE, DELETE and WITH statements are supported.");
            if(beginning)statement=tokens[i].ToUpperInvariant();
            beginning=false;
            if(statement=="UPDATE" && tokens[i].Equals("FROM",StringComparison.OrdinalIgnoreCase))throw new NotSupportedException("SQLite UPDATE FROM requires target/alias review.");
            if(tokens[i].Equals("main",StringComparison.OrdinalIgnoreCase)&&i+1<tokens.Count&&tokens[i+1]==".")throw new NotSupportedException("SQLite main schema qualification requires manual conversion.");
            if((tokens[i].Equals("INSERT",StringComparison.OrdinalIgnoreCase)||tokens[i].Equals("UPDATE",StringComparison.OrdinalIgnoreCase)) && i+1<tokens.Count && tokens[i+1].Equals("OR",StringComparison.OrdinalIgnoreCase))throw new NotSupportedException("SQLite conflict handling requires manual conversion.");
            if(tokens[i].Equals("UPDATE",StringComparison.OrdinalIgnoreCase)&&i+3<tokens.Count && tokens[i+2].Equals("AS",StringComparison.OrdinalIgnoreCase))throw new NotSupportedException("Aliased SQLite UPDATE requires manual conversion.");
        }
        return Rewrite(tokens);
    }
}
