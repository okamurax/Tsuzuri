// 部分解析の確かめ（SPEC 8.6）。tools/parse-fuzz.sh から動かす。
//
// 編集画面（MarkdownView）は、打った段落だけを Markdig で解析し直して、文書全体の解析結果に継ぎ足す（TryPatchModel・TryPatchLines）。
// 守るべき約束は「継ぎ足した結果が、全体を解析し直した結果と必ず同じ」こと。Markdown は離れた行どうしが影響し合う
// （リンクの定義・空行をまたぐ HTML・段落を割り込めない箇条書きなど）ので、例を試すだけでは取りこぼす。
// そこで、名前を付けた例（Case・LCase）を試したあと、文書をでたらめに書き換えては両者を比べることを何十万回も繰り返す。
//
// 解析そのもの（Tsuzuri/Markdown/）は本体のコードをそのままコンパイルする。継ぎ足しの判断は MarkdownView が WPF・AvalonEdit に
// 依存していて持ってこられないので、ここに写してある（Patch が同じ行の中の編集、PatchLines が行の数が変わる編集）。
// **MarkdownView の TryPatchModel・TryPatchLines・ParseBlock・MayNotInterrupt を変えたら、ここの写しも合わせて変える。**
//
//   引数: [でたらめに書き換える回数（既定 3000）] [確かめる .md ファイル（省略可。その文書の中で1文字打つのを500回試す）]
//   食い違いが1つでもあれば終了コード 1。
using System; using System.Collections.Generic; using System.Linq; using System.Text; using Tsuzuri.Markdown;
class P {
  static DocumentModel Full(List<string> lines){ var t=string.Join("\n",lines); var offs=new List<int>(); int o=0; foreach(var l in lines){offs.Add(o);o+=l.Length+1;} return MarkdownParser.Parse(t,lines,offs); }
  static bool BlankT(string s){ foreach(var c in s) if(c!=' '&&c!='\t') return false; return true; }
  static bool Ind(string t){ return t.Length>0&&(t[0]==' '||t[0]=='\t'); }
  // MarkdownView.TryPatchModel の同じ行の中の編集の写し。oldLine は書き換える前のその行。
  static bool Patch(DocumentModel model, List<string> lines, int line, string oldLine){
    if (model.Fragile || model.Lines.Length!=lines.Count) return false;
    bool blankChanged = BlankT(lines[line])!=BlankT(oldLine) || BlankT(lines[line]);
    int count=lines.Count; int from=line, to=line;
    while(from>0 && !BlankT(lines[from-1])){from--; if(line-from>400) return false;}
    while(to<count-1 && !BlankT(lines[to+1])){to++; if(to-line>400) return false;}
    var texts=new List<string>(); var offs=new List<int>(); var sb=new StringBuilder();
    for(int n=from;n<=to;n++){ offs.Add(sb.Length); texts.Add(lines[n]); sb.Append(lines[n]); if(n<to) sb.Append('\n'); }
    for(int n=from;n<=to;n++){ var now=texts[n-from]; var bef= n==line?oldLine:now; bool sn= n==from || BlankT(texts[n-1-from]); bool sB= n==from || BlankT(n-1==line?oldLine:texts[n-1-from]); if((sn&&Ind(now))||(sB&&Ind(bef))) return false; }
    if (Touch(model,from-1,-1)||Touch(model,to+1,1)) return false;
    if (model.TouchesHtml(from,to+2)) return false;
    if (MNI(texts[0]) || (line==from && MNI(oldLine))) return false;
    if (blankChanged){ string below=""; for(int n=to+1;n<model.Lines.Length;n++){ if(!BlankT(model.Lines[n].Source)){ below=model.Lines[n].Source; break; } } if(Ind(below)) return false; }
    var patch=PB(lines,from,to); if(patch==null) return false;
    bool shape=blankChanged;
    for(int i=0;i<patch.Lines.Length;i++){ var b=model.Lines[from+i]; var a=patch.Lines[i]; if(b.Kind!=a.Kind||b.Level!=a.Level||b.ContinuesHeading!=a.ContinuesHeading) shape=true; }
    if (model.Lines[line].Marker!=patch.Lines[line-from].Marker) shape=true;
    if (shape && !(Only(model.Lines,from,patch.Lines.Length)&&Only(patch.Lines,0,patch.Lines.Length))) return false;
    if (shape && MNI(Below(model,to))) return false;
    for(int i=0;i<patch.Lines.Length;i++){ patch.Lines[i].Depth=model.Lines[from+i].Depth; model.Lines[from+i]=patch.Lines[i]; model.Inlines[from+i]=patch.Inlines[i]; }
    if (shape) { model.RecomputeDepths(); shapeCount++; }
    return true;
  }

  static int LineOf(List<int> offs,int o){ int lo=0,hi=offs.Count-1; while(lo<hi){ int m=(lo+hi+1)/2; if(offs[m]<=o) lo=m; else hi=m-1;} return lo; }
  static int LineOf2(List<string> ls,int off){ int o=0; for(int i=0;i<ls.Count;i++){ if(off<=o+ls[i].Length) return i; o+=ls[i].Length+1;} return ls.Count-1; }
  static DocumentModel PB(List<string> lines,int from,int to){ int lead=from>0?1:0, trail=to<lines.Count-1?1:0; var all=lines.GetRange(from-lead,to-from+1+lead+trail); var offs=new List<int>(); var sb=new StringBuilder(); for(int i=0;i<all.Count;i++){ offs.Add(sb.Length); sb.Append(all[i]); if(i<all.Count-1) sb.Append('\n'); }
    var p=MarkdownParser.Parse(sb.ToString(),all,offs); if(p.Fragile||p.HtmlRanges.Count>0||p.Lines.Length!=all.Count) return null; int n=to-from+1; var l=new LineInfo[n]; var ii=new InlineScan[n]; Array.Copy(p.Lines,lead,l,0,n); Array.Copy(p.Inlines,lead,ii,0,n); return new DocumentModel(l,ii,0); }
  static readonly System.Text.RegularExpressions.Regex IS=new System.Text.RegularExpressions.Regex(@"^ {0,3}(?:[-+*]|[0-9]{1,9}[.)])(?:[ \t]|$)|^ {0,3}<");
  static bool MNI(string t){ return IS.IsMatch(t); }
  static string Below(DocumentModel m,int to){ for(int n=to+1;n<m.Lines.Length;n++) if(!BlankT(m.Lines[n].Source)) return m.Lines[n].Source; return ""; }
  static int linePatches=0;
  // MarkdownView.TryPatchLines の写し（行は 0 から数える）。lines は書き換えたあとの行。
  static bool PatchLines(ref DocumentModel model, List<string> lines, int off, string removed, string inserted){
    if (model.Fragile) return false;
    var old=model.Lines; int count=lines.Count; int delta=count-old.Length; int end=off+inserted.Length;
    var offs=new List<int>(); int o=0; foreach(var l in lines){offs.Add(o);o+=l.Length+1;} int total=o-1;
    if(off<0||end>total) return false;
    int first=LineOf(offs,off), last=LineOf(offs,end);
    int from=first; while(from>0&&!BlankT(lines[from-1])){from--; if(first-from>400) return false;}
    int to=last; while(to<count-1&&!BlankT(lines[to+1])){to++; if(to-last>400) return false;}
    int oldTo=to-delta; if(oldTo<from||oldTo>old.Length-1) return false;
    int start=offs[from], stop=offs[to]+lines[to].Length; if(off<start||end>stop) return false;
    var joined=string.Join("\n",lines);
    var restored=joined.Substring(start,off-start)+removed+joined.Substring(end,stop-end);
    var oldText=new StringBuilder(); for(int n=from;n<=oldTo;n++){ if(n>from) oldText.Append('\n'); oldText.Append(old[n].Source);} 
    if(restored!=oldText.ToString()) return false;
    if(from>0&&lines[from-1]!=old[from-1].Source) return false;
    if(to<count-1&&lines[to+1]!=old[oldTo+1].Source) return false;
    var texts=new List<string>(); var toffs=new List<int>(); var sb=new StringBuilder();
    for(int n=from;n<=to;n++){ toffs.Add(sb.Length); texts.Add(lines[n]); sb.Append(lines[n]); if(n<to) sb.Append('\n'); }
    for(int i=0;i<texts.Count;i++) if((i==0||BlankT(texts[i-1]))&&Ind(texts[i])) return false;
    for(int n=from;n<=oldTo;n++) if((n==from||BlankT(old[n-1].Source))&&Ind(old[n].Source)) return false;
    if(Touch(model,from-1,-1)||Touch(model,oldTo+1,1)) return false;
    if(model.TouchesHtml(from,oldTo+2)) return false;
    if(MNI(texts[0])||MNI(old[from].Source)) return false;
    { string below=""; for(int n=oldTo+1;n<old.Length;n++){ if(!BlankT(old[n].Source)){ below=old[n].Source; break; } } if(Ind(below)) return false; }
    var patch=PB(lines,from,to); if(patch==null) return false;
    if(!Only(old,from,oldTo-from+1)||!Only(patch.Lines,0,patch.Lines.Length)) return false;
    if(MNI(Below(model,oldTo))) return false;
    var nl=new LineInfo[count]; var ni=new InlineScan[count];
    Array.Copy(old,0,nl,0,from); Array.Copy(model.Inlines,0,ni,0,from);
    Array.Copy(patch.Lines,0,nl,from,patch.Lines.Length); Array.Copy(patch.Inlines,0,ni,from,patch.Inlines.Length);
    Array.Copy(old,oldTo+1,nl,to+1,old.Length-oldTo-1); Array.Copy(model.Inlines,oldTo+1,ni,to+1,old.Length-oldTo-1);
    var m=new DocumentModel(nl,ni,0); m.RecomputeDepths(); foreach(var r in model.HtmlRanges) m.HtmlRanges.Add(r.Key>oldTo? new KeyValuePair<int,int>(r.Key+delta,r.Value+delta):r); model=m; linePatches++;
    return true;
  }
  static int lcase=0;
  static void LCase(string name,string[] doc,int off,int remLen,string ins){
    var text=string.Join("\n",doc); var lines=doc.ToList(); var model=Full(lines);
    var removed=text.Substring(off,remLen); var nt=text.Substring(0,off)+ins+text.Substring(off+remLen); var nlines=nt.Split('\n').ToList();
    var ok=PatchLines(ref model,nlines,off,removed,ins); var full=Full(nlines); int d=ok?Diff(model,full):-1;
    Console.WriteLine((d!=-1?"BAD  ":"ok   ")+(ok?"patched ":"full    ")+name+(d!=-1?" line "+d:"")); if(d!=-1) lcase++;
  }
  static int shapeCount=0;
  static bool Touch(DocumentModel m,int st,int step){ for(int n=st;n>=0&&n<m.Lines.Length;n+=step){ var k=m.Lines[n].Kind; if(k==LineKind.Code||k==LineKind.Fence) return true; if(!BlankT(m.Lines[n].Source)) return false; } return false; }
  static readonly System.Text.RegularExpressions.Regex Atx=new System.Text.RegularExpressions.Regex(@"^ {0,3}#{1,6}(?:[ \t]|$)");
  static bool Only(LineInfo[] l,int st,int c){ for(int i=st;i<st+c;i++){ var k=l[i].Kind; if(k==LineKind.Heading&&!l[i].ContinuesHeading&&Atx.IsMatch(l[i].Source)) continue; if(k==LineKind.Paragraph||k==LineKind.Blank) continue; return false;} return true; }
  static string Sig(LineInfo l, InlineScan s){ var sb=new StringBuilder(); sb.Append(l.Kind).Append('|').Append(l.Level).Append('|').Append(l.Depth).Append('|').Append(l.Marker).Append('|').Append(l.Content).Append('|').Append(l.IsChecked).Append('|').Append(l.ContinuesHeading);
    foreach(var x in s.Spans) sb.Append(';').Append(x.Start).Append(',').Append(x.Length).Append(',').Append(x.Kind);
    foreach(var x in s.Links) sb.Append(';').Append(x.Start).Append(',').Append(x.Length).Append(',').Append(x.Target).Append(',').Append(x.IsImage);
    return sb.ToString(); }
  static int Diff(DocumentModel a, DocumentModel b){ if(a.Fragile!=b.Fragile) return -2; if(string.Join(",",a.HtmlRanges)!=string.Join(",",b.HtmlRanges)) return -4; if(a.MaxDepth!=b.MaxDepth) return -3; for(int i=0;i<a.Lines.Length;i++) if(Sig(a.Lines[i],a.Inlines[i])!=Sig(b.Lines[i],b.Inlines[i])) return i; return -1; }
  static int caseBad=0;
  static void Case(string name, string[] doc, int ln, string newText){
    var lines=doc.ToList(); var model=Full(lines); var old=lines[ln]; lines[ln]=newText;
    if(old.Contains('\n')||newText.Contains('\n')) return;
    var ok=Patch(model,lines,ln,old); var full=Full(lines); int d=ok?Diff(model,full):-1;
    Console.WriteLine((d!=-1?"BAD  ":"ok   ")+(ok?"patched ":"full    ")+name+(d!=-1?" line "+d:""));
    if(d!=-1) caseBad++;
  }
  static int Main(string[] args){
    int rounds=args.Length>0?int.Parse(args[0]):3000;
    var logmd=args.Length>1?args[1]:null;
    if(logmd!=null){ var ls=System.IO.File.ReadAllText(logmd).Replace("\r\n","\n").Split('\n').ToList(); var m=Full(ls); Console.WriteLine("lines="+ls.Count+" fragile="+m.Fragile+" htmlRanges="+m.HtmlRanges.Count);
      var r=new Random(3); int ok=0,ng=0,bd=0; for(int k=0;k<500;k++){ int ln=r.Next(ls.Count); if(BlankT(ls[ln])) continue; var old=ls[ln]; ls[ln]=old+"テ"; if(Patch(m,ls,ln,old)) ok++; else { ng++; m=Full(ls);} var f=Full(ls); if(Diff(m,f)!=-1){bd++; m=f;} }
      Console.WriteLine("patched="+ok+" full="+ng+" bad="+bd); if(bd>0) return 1; }
    Case("fence in item dedent close", new[]{"- a","","  ```","  code","  ```","","para","","# H"}, 4, " ```");
    Case("fence in item dedent close0", new[]{"- a","","  ```","  code","  ```","","para","","# H"}, 4, "```");
    Case("item fence open dedent", new[]{"- a","","  ```","  code","  ```","","# H"}, 2, " ```");
    Case("indented code in item becomes", new[]{"- a","","      ```","      x","      ```","","# H"}, 0, " - a");
    Case("linkdef added", new[]{"para","","[a]: /x"}, 2, "[a]: /xy");
    Case("linkdef new", new[]{"para [a]","","xa"}, 2, "[a]: /x");
    Case("footnote def", new[]{"para [^1]","","[^1]: x"}, 2, "[^1]: xy");
    Case("html", new[]{"<div>","","x"}, 2, "xy");
    Case("ideo space emph", new[]{"*abc","　","def*"}, 0, "abc");
    Case("ideo space code", new[]{"`abc","　","def`"}, 2, "def");
    Case("ideo setext", new[]{"abc","　","def","==="}, 2, "dex");
    Case("ideo list lazy", new[]{"- a","　","    x"}, 0, "- ab");
    Case("nbsp line", new[]{"*abc"," ","def*"}, 2, "def");
    Case("list indent ->heading", new[]{"- a","","      # Title"}, 0, " - a");
    Case("ordered 1->10", new[]{"1. a","","       # Title"}, 0, "10. a");
    Case("ordered 10->1", new[]{"10. a","","       # Title"}, 0, "1. a");
    Case("list code->para", new[]{"- a","","      *x*"}, 0, " - a");
    Case("fence blank inside", new[]{"```","a","","    b","```"}, 3, "    bc");
    Case("emph in item", new[]{"- a *b","","  c*"}, 0, "- a b");
    Case("tab marker", new[]{"-\ta","","      # T"}, 0, "- \ta");
    Case("dedent continuation", new[]{"- a","","  p1","","      code"}, 2, "p1");
    Case("para->h1", new[]{"# A","","text","","more","","## B","x"}, 2, "# text");
    Case("h2->para", new[]{"# A","","## t","","more"}, 2, "t");
    Case("para->h in list run", new[]{"- a","b","","    x"}, 1, "# b");
    Case("setext para", new[]{"a","---","","x"}, 0, "# a");
    Case("heading in quote", new[]{"> a","","x"}, 0, "> # a");
    Case("para->h breaks lazy", new[]{"- a","b","","  c"}, 1, "# b");
    Case("type into blank between paras", new[]{"a","","b"}, 1, "日本語");
    Case("type into blank after heading", new[]{"# H","","b","","## I","c"}, 1, "本文");
    Case("type into blank before indented", new[]{"- a","","","    x"}, 1, "y");
    Case("type into blank before list cont", new[]{"- a","","","  cont","","    four"}, 2, "y");
    Case("clear para line", new[]{"a","b","c","","    d"}, 1, "");
    Case("blank to ideo", new[]{"- a","  b","","      x"}, 2, "　");
    Console.WriteLine("case bad="+caseBad);
    LCase("enter mid para", new[]{"# H","","abcdef","ghi","","## I","x"}, 7, 0, "\n");
    LCase("enter end heading", new[]{"# H","text","","## I","x"}, 3, 0, "\n");
    LCase("enter blank after heading", new[]{"# H","text","","## I","x"}, 3, 0, "\n\n");
    LCase("backspace join", new[]{"# H","","text","more","","## I"}, 9, 1, "");
    LCase("backspace remove blank", new[]{"# H","","text","","more","","## I"}, 9, 1, "");
    LCase("join heading into para", new[]{"para","# H","x"}, 4, 1, "");
    LCase("enter before list", new[]{"a","- b","","  c"}, 1, 0, "\n");
    LCase("remove blank before indented", new[]{"- a","","x","","    y"}, 4, 2, "");
    LCase("enter setext", new[]{"abc---","","x"}, 3, 0, "\n");
    LCase("enter table", new[]{"|a|b||---|---|","","x"}, 5, 0, "\n");
    LCase("enter fence", new[]{"text```","","x"}, 4, 0, "\n");
    LCase("linkdef at block end", new[]{"## 節","","- 項目1"}, 2, 0, "\n[a]: /x ");
    LCase("delete blank merges list", new[]{"- a","","p","","- b"}, 4, 3, "");
    Console.WriteLine("line case bad="+lcase);

    var seed=new[]{
     "# 見出し","","本文 **強い** と *斜め* と `code` と [リンク](a.md#x)。","続きの行 ~~消し~~","　","全角空白の後の行 *x*","","## 節","","- 項目1","- 項目2 **太**","  - 入れ子","    - さらに","","  続きの段落（リスト内）","","      深い字下げ # 見出しもどき","","1. 番号","2) 番号2","","       # Title","","10. 十","","- [ ] タスク","- [x] 済み","","> 引用 *a*","> 続き","","```","コード *not*","","まだコード","```","","- a","","  ```","  code","  ```","","| a | b |","|---|---|","| 1 | 2 |","","段落","===","","段落2","---","","    インデントコード","","![画像](<a b.png>)","","### 小","テキスト<http://example.com> と http://x.com","","* 星リスト","+ プラス",""," 先頭空白の段落","","***","","<script>","x = 1;","","y","</script>","","<!-- メモ","","d -->","","<a href=\"x\">リンク</a>","","<div>","a","</div>","","段落の後","<a href=\"y\">b</a>","",
    };
    var original=new List<string>(); for(int k=0;k<40;k++) original.AddRange(seed);
    var rnd=new Random(11);
    var chars="ab *_`#->|[]()!~\\:1.x0\t　 <".ToCharArray();
    int patched=0, fallback=0, bad=0;
    for(int round=0; round<rounds; round++){
      var lines=new List<string>(original); var model=Full(lines);
      for(int step=0; step<15; step++){   // 継ぎ足した結果を次の編集に持ち越す（食い違いが溜まるかも見る）
        if(rnd.Next(3)==0){
          var text=string.Join("\n",lines); int off=rnd.Next(text.Length+1); int rem=0; string ins;
          int lm=rnd.Next(6);
          if(lm==0) ins="\n";
          else if(lm==1) ins="\n\n";
          else if(lm==2) ins="\n"+new[]{"日本語","# 見出し","- a","x","　","    c","```","---","|a|"," t","[a]: /x","<script>","</script>","<!--","-->","<div>","<a href=x>"}[rnd.Next(17)];
          else if(lm==3){ int p=text.IndexOf('\n',Math.Max(0,off-1)); if(p<0){ins="\n";} else {off=p; rem=1; ins="";} }
          else if(lm==4){ off=rnd.Next(text.Length); rem=Math.Min(text.Length-off,1+rnd.Next(30)); ins=""; }
          else { int p=text.IndexOf('\n',off); if(p<0) p=text.Length; off=p; ins="\n"; }
          var removed=text.Substring(off,rem); var nt=text.Substring(0,off)+ins+text.Substring(off+rem);
          lines=nt.Split('\n').ToList();
          if(PatchLines(ref model,lines,off,removed,ins)) patched++; else { fallback++; model=Full(lines); }
          var fl=Full(lines); int dd=Diff(model,fl);
          if(dd!=-1){ bad++; if(bad<=6){ Console.WriteLine("LINE MISMATCH round "+round+" at "+dd+" off="+off+" rem='"+removed.Replace("\n","\\n")+"' ins='"+ins.Replace("\n","\\n")+"'"); if(dd==-2){ int L=LineOf2(lines,off); for(int j=Math.Max(0,L-6);j<Math.Min(lines.Count,L+6);j++) Console.WriteLine("    "+j+" ["+fl.Lines[j].Kind+"] '"+lines[j]+"'"); } if(dd>=0){ Console.WriteLine("  patched: "+Sig(model.Lines[dd],model.Inlines[dd])+"\n  full:    "+Sig(fl.Lines[dd],fl.Inlines[dd])); for(int j=Math.Max(0,dd-8);j<Math.Min(lines.Count,dd+3);j++) Console.WriteLine("    "+j+" ["+fl.Lines[j].Kind+"/"+model.Lines[j].Kind+"] '"+lines[j]+"'"); } }
            model=fl; }
          continue;
        }
        int ln=rnd.Next(lines.Count); var s=lines[ln]; var old=s;
        int mode=rnd.Next(11);
        if(mode==0 && s.Length>0){ int p=rnd.Next(s.Length); s=s.Remove(p,1);} 
        else if(mode==1){ s=" "+s; }
        else if(mode==2 && s.Length>0 && s[0]==' '){ s=s.Substring(1); }
        else if(mode==5){ s=new string('#',1+rnd.Next(3))+" "+s; }
        else if(mode==6 && s.StartsWith("#")){ s=s.Substring(1); }
        else if(mode==7 && s.Length>0){ s="#"+s; }
        else if(mode==8){ for(int t=0;t<50;t++){ int c=rnd.Next(lines.Count); if(BlankT(lines[c])){ ln=c; s=lines[c]; old=s; break; } } s=s+new[]{"日本語","x","　","# 見出し","- a","*強*","1. n","> q","```","---","|a|"," t","    c","<script>","</script>","<!--","-->","<div>","<a href=x>y</a>"}[rnd.Next(19)]; }
        else if(mode==9){ s=""; }
        else if(mode==10){ s="　"; }
        else { int p=rnd.Next(s.Length+1); s=s.Insert(p, chars[rnd.Next(chars.Length)].ToString()); }
        lines[ln]=s;
        if(Patch(model,lines,ln,old)) patched++; else { fallback++; model=Full(lines); }
        var full=Full(lines); int d=Diff(model,full);
        if(d!=-1){ bad++; if(bad<=6){ Console.WriteLine("MISMATCH round "+round+" step "+step+" edit line "+ln+" at "+d+" old='"+old+"' new='"+s+"'"); if(d>=0){Console.WriteLine("  patched: "+Sig(model.Lines[d],model.Inlines[d])+"\n  full:    "+Sig(full.Lines[d],full.Inlines[d])); for(int j=Math.Max(0,ln-8);j<Math.Min(lines.Count,d+3);j++) Console.WriteLine("    "+j+" ["+full.Lines[j].Kind+"/"+model.Lines[j].Kind+"] '"+lines[j]+"'");} }
          model=full; }
      }
    }
    Console.WriteLine("shape patches="+shapeCount);
    Console.WriteLine("line patches="+linePatches);
    Console.WriteLine("patched="+patched+" fallback="+fallback+" bad="+bad);
    var failed=caseBad+lcase+bad;
    Console.WriteLine(failed==0?"OK":"NG（食い違い "+failed+" 件）");
    return failed==0?0:1;
  }
}
