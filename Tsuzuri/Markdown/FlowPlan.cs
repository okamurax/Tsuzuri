using System;
using System.Collections.Generic;
using System.Linq;

namespace Tsuzuri.Markdown
{
    /// <summary>
    /// 自動で送るときの段取り（2026-11-17。SPEC 21章）。送り始めたカードから、つながりをたどって次に送るカードを1枚ずつ決める。
    /// 画面やターミナルに触らない部分（tools/flow-file で確かめる）。送るのは MainWindow.Flow.cs。
    ///
    /// ・行けるカード: 送り始めたカードから線をたどって行ける Claude・結果・確認のカード（添付のカードへの線はたどらない）
    /// ・戻る線: 送り始めたカードから深さ優先でたどったとき、たどっている途中のカードへ戻る線（繰り返し）。それ以外の線は前へ進む線
    /// ・カードは、前へ進む線で入ってくる線（行けるカードからのもの）が全部決まったら（進んだ・進まなかった）決まる。
    ///   1本でも進んだら送る（結果のカードは送らずに通り抜ける）。どれも進まなかったら飛ばす（その先の線も進まない）
    /// ・済んだカードの線: 分かれ道（FlowResults.Choices）なら、選んだ名前の線と名前の無い線だけ進む。そうでなければ全部進む
    /// ・戻る線が進んだら、戻り先から前へ進む線で行けるカードを、まだ済んでいないことにして戻り先から送り直す。戻るのは1つの戻り先につき LoopLimit 回まで
    /// ・送れるカードが2枚以上あれば、ページの並びの順
    /// 段取りは送り始めたときのつながりで決める（途中でつなぎ直しても、その送りには効かない）。
    /// </summary>
    public sealed class FlowPlan
    {
        /// <summary>1つの戻り先へ戻る回数の上限。</summary>
        public const int LoopLimit = 5;

        private enum State
        {
            Waiting,
            Ready,
            Running,
            Done,
            Skipped,
        }

        private sealed class Node
        {
            public string Title;
            public FlowKind Kind;
            public int Index;
            public List<string> Choices;
            public readonly List<Link> Out = new List<Link>();
            public readonly List<Link> In = new List<Link>();   // 前へ進む線だけ
            public State State;
            public int Loops;
        }

        private sealed class Link
        {
            public Node From;
            public Node To;
            public string Label;
            public bool Back;
            public bool? Fired;   // null はまだ決まっていない
        }

        private readonly Dictionary<string, Node> _nodes = new Dictionary<string, Node>();
        private readonly List<Node> _order = new List<Node>();

        /// <summary>送りを続けられなくなった理由（戻る回数を越えた・知らない行き先）。無ければ null。</summary>
        public string Problem { get; private set; }

        public FlowPlan(FlowFile flow, FlowCard start)
        {
            // 行けるカード
            var stack = new Stack<FlowCard>();
            Add(start, flow);
            stack.Push(start);
            while (stack.Count > 0)
            {
                foreach (var edge in stack.Pop().Edges)
                {
                    var target = flow.Find(edge.Target);
                    if (target == null || target.Kind == FlowKind.Files || _nodes.ContainsKey(target.Title)) continue;
                    Add(target, flow);
                    stack.Push(target);
                }
            }

            foreach (var card in flow.Cards)
            {
                Node from;
                if (!_nodes.TryGetValue(card.Title, out from)) continue;
                foreach (var edge in card.Edges)
                {
                    Node to;
                    if (!_nodes.TryGetValue(edge.Target, out to)) continue;
                    from.Out.Add(new Link { From = from, To = to, Label = edge.Label });
                }
            }

            // 戻る線（深さ優先で、たどっている途中のカードへの線）
            var visiting = new HashSet<Node>();
            var visited = new HashSet<Node>();
            MarkBack(_nodes[start.Title], visiting, visited);
            foreach (var link in _nodes.Values.SelectMany(node => node.Out).Where(link => !link.Back)) link.To.In.Add(link);

            _order.AddRange(_nodes.Values.OrderBy(node => node.Index));
            _nodes[start.Title].State = State.Ready;
        }

        private void Add(FlowCard card, FlowFile flow)
        {
            _nodes[card.Title] = new Node
            {
                Title = card.Title,
                Kind = card.Kind,
                Index = flow.Cards.IndexOf(card),
                Choices = FlowResults.Choices(flow, card),
            };
        }

        private static void MarkBack(Node node, HashSet<Node> visiting, HashSet<Node> visited)
        {
            visiting.Add(node);
            foreach (var link in node.Out)
            {
                if (visiting.Contains(link.To)) link.Back = true;
                else if (!visited.Contains(link.To)) MarkBack(link.To, visiting, visited);
            }
            visiting.Remove(node);
            visited.Add(node);
        }

        /// <summary>
        /// 次に送る（確認のカードなら止まる）カードの題。無ければ（送り終えた・続けられない）null。返したカードは、済んだら Finish を呼ぶ。
        /// </summary>
        public string Next()
        {
            if (Problem != null || _order.Any(node => node.State == State.Running)) return null;
            Settle();
            var next = _order.FirstOrDefault(node => node.State == State.Ready);
            if (next == null) return null;
            next.State = State.Running;
            return next.Title;
        }

        /// <summary>まだ送るかもしれないカード（Claude・確認）の数（「残り N 枚以内」）。</summary>
        public int Remaining
        {
            get { return _order.Count(node => (node.State == State.Waiting || node.State == State.Ready) && node.Kind != FlowKind.Result); }
        }

        /// <summary>そのカードの行き先の名前（分かれ道でなければ空）。</summary>
        public List<string> ChoicesOf(string title)
        {
            Node node;
            return _nodes.TryGetValue(title, out node) ? node.Choices : new List<string>();
        }

        /// <summary>
        /// 送ったカード（確認のカードは続けたとき）が済んだ。分かれ道なら choice は選んだ線の名前（ChoicesOf のどれか）。
        /// 続けられなくなったら false（Problem に理由）。
        /// </summary>
        public bool Finish(string title, string choice)
        {
            Node node;
            if (!_nodes.TryGetValue(title, out node) || node.State != State.Running)
            {
                Problem = "送っていないカード " + title + " が済んだことになりました";
                return false;
            }
            if (node.Choices.Count > 0 && (choice == null || !node.Choices.Contains(choice)))
            {
                Problem = "行き先が決まりませんでした（" + (choice ?? "選んでいない") + "）";
                return false;
            }
            node.State = State.Done;
            Leave(node, choice);
            return Problem == null;
        }

        /// <summary>済んだ・通り抜けたカードから出る線を決める。</summary>
        private void Leave(Node node, string choice)
        {
            // 前へ進む線を先に決める（戻る線で戻すと、戻した先の線はまた決まっていないことになる）
            foreach (var link in node.Out.Where(link => !link.Back)) link.Fired = Goes(node, link, choice);
            foreach (var link in node.Out.Where(link => link.Back && Goes(node, link, choice)).ToList())
            {
                Loop(link.To);
                if (Problem != null) return;
            }
        }

        private static bool Goes(Node node, Link link, string choice)
        {
            return node.Choices.Count == 0 || link.Label.Length == 0 || !node.Choices.Contains(link.Label) || link.Label == choice;
        }

        /// <summary>戻る線で戻る。戻り先から前へ進む線で行けるカードを、まだ済んでいないことにする。</summary>
        private void Loop(Node target)
        {
            if (target.Loops >= LoopLimit)
            {
                Problem = "同じところへ " + LoopLimit + " 回戻ったので止めました（繰り返しの上限）";
                return;
            }
            target.Loops++;

            var region = new HashSet<Node>();
            var stack = new Stack<Node>();
            region.Add(target);
            stack.Push(target);
            while (stack.Count > 0)
            {
                foreach (var link in stack.Pop().Out.Where(link => !link.Back))
                {
                    if (region.Add(link.To)) stack.Push(link.To);
                }
            }
            foreach (var node in region)
            {
                if (node.State != State.Running) node.State = State.Waiting;
                foreach (var link in node.Out.Where(link => !link.Back)) link.Fired = null;
            }
            if (target.Kind == FlowKind.Result)
            {
                target.State = State.Done;   // 結果のカードへ戻ったら、送らずに通り抜ける
                Leave(target, null);
            }
            else
            {
                target.State = State.Ready;
            }
        }

        /// <summary>入ってくる線が全部決まったカードを、送る・通り抜ける・飛ばすに決める。変わらなくなるまで。</summary>
        private void Settle()
        {
            var changed = true;
            while (changed && Problem == null)
            {
                changed = false;
                foreach (var node in _order)
                {
                    if (node.State != State.Waiting || node.In.Count == 0 || node.In.Any(link => link.Fired == null)) continue;
                    changed = true;
                    if (!node.In.Any(link => link.Fired == true))
                    {
                        node.State = State.Skipped;
                        foreach (var link in node.Out.Where(link => !link.Back)) link.Fired = false;
                    }
                    else if (node.Kind == FlowKind.Result)
                    {
                        node.State = State.Done;   // 結果のカードは送らない。中身は前のカードの答えから入る
                        Leave(node, null);
                    }
                    else
                    {
                        node.State = State.Ready;
                    }
                }
            }
        }
    }
}
