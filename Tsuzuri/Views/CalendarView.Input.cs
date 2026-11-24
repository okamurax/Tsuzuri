using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Tsuzuri.Markdown;

namespace Tsuzuri.Views
{
    // CalendarView の一部: マウス（帯をつかむ・日を選ぶ）・マスで名前を打って足す・アウトラインの見出しを落とす・キー（2026-11-21 に CalendarView.cs から分けた）。
    public sealed partial class CalendarView
    {
        // ---------------------------------------------------------------
        // マウス
        // ---------------------------------------------------------------

        /// <summary>押した場所の日（_weeks の中の位置）。上下の外なら端の週に寄せる。</summary>
        private DateTime? DateAt(Point point)
        {
            if (_rows.Count == 0) return null;

            var top = 0.0;
            var hit = _rows[_rows.Count - 1];
            if (point.Y < 0) hit = _rows[0];
            else
            {
                foreach (var pair in _rows)
                {
                    var height = pair.Value.ActualHeight;
                    if (point.Y < top + height)
                    {
                        hit = pair;
                        break;
                    }
                    top += height;
                }
            }

            var width = hit.Value.ActualWidth;
            if (width <= 0) return null;
            var column = Math.Max(0, Math.Min(6, (int)Math.Floor(point.X / (width / 7))));
            return hit.Key.AddDays(column);
        }

        private void OnBarDown(ScheduleOccurrence occ, Border bar, MouseButtonEventArgs e, bool startsHere, bool endsHere)
        {
            var item = occ.Item;
            e.Handled = true;
            if (_editorOpen) return;

            // 押した端と日は、組み直す前に読む（マスで打っていた名前を足すと組み直し、この帯は画面から外れる）
            var gesture = EdgeOf(bar, e, startsHere, endsHere);
            var date = DateAt(e.GetPosition(_weeks));
            CommitNew();
            Focus();

            if (e.ClickCount == 2)
            {
                // どの予定も欄を出す。見出しと結んだ予定は、名前の代わりにリンク先と「見出しへ飛ぶ」（2026-11-21。前は2度押すとすぐ飛び、消す入口が無かった）
                OpenEditor(item, occ.Original, occ.Start, occ.End);
                return;
            }
            if (date == null) return;

            ClearDays();   // 予定を選んだら、日を選ぶのはやめる
            if (!IsSelected(occ))
            {
                _selected = item;
                _selectedOriginal = occ.Original;
                Rebuild();
            }

            _gesture = gesture;
            _dragItem = item;
            _dragOriginal = occ.Original;
            _dragShownStart = occ.Start;
            _dragFrom = date.Value;
            _dragDelta = 0;
            _weeks.CaptureMouse();
        }

        private void OnWeeksDown(object sender, MouseButtonEventArgs e)
        {
            if (_editorOpen) return;
            if (_newBox != null && _newBox.IsMouseOver) return;   // 打っている欄の中を押した

            var date = DateAt(e.GetPosition(_weeks));
            if (date == null) return;
            e.Handled = true;

            CommitNew();
            Focus();

            if (e.ClickCount == 2)
            {
                // その日のマスで名前を打つ（1日の予定。何日かにするのは、あとで帯の端を引っぱる）
                _selected = null;
                _newOn = date.Value;
                Rebuild();
                return;
            }

            if (_selected != null)
            {
                _selected = null;
                Rebuild();
            }

            // 日を選ぶ。Shift で押すと、選び始めた日からその日まで。押したままなぞっても延ばせる
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0 && _dayAnchor.HasValue) _dayEnd = date.Value;
            else
            {
                _dayAnchor = date.Value;
                _dayEnd = date.Value;
                _selectingDays = true;
                _weeks.CaptureMouse();
            }
            PaintCells();
        }

        private void OnWeeksMove(object sender, MouseEventArgs e)
        {
            if (!_weeks.IsMouseCaptured || (_dragItem == null && !_selectingDays)) return;

            // 上下の端へ寄せたら、少しずつスクロールする（見えていない週まで延ばす・動かす・選ぶため）
            var inView = e.GetPosition(_scroll).Y;
            if (inView < 24) _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset - 16);
            else if (inView > _scroll.ViewportHeight - 24) _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset + 16);

            var date = DateAt(e.GetPosition(_weeks));
            if (date == null) return;

            if (_selectingDays)
            {
                if (date != _dayEnd)
                {
                    _dayEnd = date;
                    PaintCells();
                }
                return;
            }
            var delta = (int)(date.Value - _dragFrom).TotalDays;
            if (delta == _dragDelta) return;
            _dragDelta = delta;
            RebuildVisible();   // 見えている週だけ。離したら全部組み直す（EndGesture）
        }

        private void OnWeeksUp(object sender, MouseButtonEventArgs e)
        {
            if (!_weeks.IsMouseCaptured) return;
            EndGesture(true);
        }

        /// <summary>
        /// 帯をつかむのを終える。commit なら、動かした・延ばした形を保存する。
        /// 繰り返しの予定の回を動かしたときは、離した所でメニューを出し「この回だけ／これ以降／全部」を尋ねる（2026-11-23。利用者の考え。Google カレンダーと同じ）。
        /// 尋ねている間は、動かした先の形のまま出しておく（つかんだままの扱い。読み直し・組み直しもしない）。
        /// </summary>
        private void EndGesture(bool commit)
        {
            if (_askingScope) return;   // マウスを放した（LostMouseCapture）。尋ねるメニューが答えを待っている
            if (commit && _dragItem != null && _dragItem.IsRepeat && _dragOriginal.HasValue && _gesture == ScheduleGesture.Move && _dragDelta != 0)
            {
                AskMoveScope();
                return;
            }
            FinishGesture(commit, ScheduleMoveScope.One);
        }

        /// <summary>繰り返しの回を動かした範囲を尋ねる。選ばずに閉じたら（Esc・ほかの所を押した）やめる。</summary>
        private void AskMoveScope()
        {
            _askingScope = true;
            if (_weeks.IsMouseCaptured) _weeks.ReleaseMouseCapture();

            ScheduleMoveScope? chosen = null;
            var menu = new ContextMenu { PlacementTarget = _weeks, Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
            menu.Items.Add(FilerView.MenuTitle("繰り返しの予定を動かす"));
            menu.Items.Add(new Separator());
            foreach (var choice in new[]
            {
                new { Scope = ScheduleMoveScope.One, Header = "この回だけ", Tip = "ほかの回はそのまま" },
                new { Scope = ScheduleMoveScope.Following, Header = "これ以降の回", Tip = "この回から後を同じ日数ずらす（前の回はそのまま。予定は2つに分かれる）" },
                new { Scope = ScheduleMoveScope.All, Header = "全部の回", Tip = "1回目から全部を同じ日数ずらす" },
            })
            {
                var scope = choice.Scope;
                var entry = new MenuItem { Header = choice.Header, ToolTip = choice.Tip };
                entry.Click += delegate { chosen = scope; };
                menu.Items.Add(entry);
            }
            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = "やめる" });
            // 押した項目の Click は閉じる前に来るが、閉じたあとで決める（念のため後へ回す）
            menu.Closed += delegate
            {
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    _askingScope = false;
                    FinishGesture(chosen.HasValue, chosen ?? ScheduleMoveScope.One);
                }), DispatcherPriority.Input);
            };
            menu.IsOpen = true;
        }

        private void FinishGesture(bool commit, ScheduleMoveScope scope)
        {
            var item = _dragItem;
            var gesture = _gesture;
            var delta = _dragDelta;

            _dragItem = null;
            _gesture = ScheduleGesture.None;
            _dragDelta = 0;
            _selectingDays = false;
            if (_weeks.IsMouseCaptured) _weeks.ReleaseMouseCapture();
            if (item == null) return;

            var original = _dragOriginal;
            var shownStart = _dragShownStart;
            _dragOriginal = null;
            var key = ScheduleBook.KeyOf(item);
            if (commit && delta != 0 && Sync())
            {
                var target = _book.Find(item, key);
                if (target == null)
                {
                    Gone();
                    return;
                }
                if (_book.Drag(target, original, shownStart, gesture, delta, scope))
                {
                    Save();
                    if (_book.LastNote != null) _status(_book.LastNote);
                    else if (scope != ScheduleMoveScope.One && gesture == ScheduleGesture.Move && original.HasValue)
                        _status((scope == ScheduleMoveScope.All ? "全部の回" : "これ以降の回") + "をずらしました（Ctrl+Z で戻せます）: " + target.Title);
                }
            }
            Rebuild();
        }

        // ---------------------------------------------------------------
        // その日のマスで名前を打つ（足す）
        // ---------------------------------------------------------------

        private TextBox NewBox()
        {
            if (_newBox != null) return Detach(_newBox);

            var box = new TextBox
            {
                Margin = new Thickness(3, 1, 3, 1),
                Padding = new Thickness(4, 0, 4, 0),
                VerticalContentAlignment = VerticalAlignment.Center,
                Background = Resource("PageBackground"),
                Foreground = Resource("TextPrimary"),
                CaretBrush = Resource("TextPrimary"),
                BorderBrush = Resource("AccentBrush"),
                BorderThickness = new Thickness(1),
                ToolTip = "予定の名前（時間もここに）。Enter で足す、Esc でやめる。何日かにするのは、足したあとで帯の端を引っぱる",
            };
            box.KeyDown += (sender, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    e.Handled = true;
                    CommitNew();
                }
                else if (e.Key == Key.Escape)
                {
                    e.Handled = true;
                    CancelNew();
                }
            };
            box.LostKeyboardFocus += (sender, e) =>
            {
                // ほかの所を押した・別の窓へ移った。打った名前は足す（捨てない）
                if (ReferenceEquals(box, _newBox) && !box.IsKeyboardFocusWithin && !_extending)
                    Dispatcher.BeginInvoke(new Action(delegate { if (ReferenceEquals(box, _newBox) && !box.IsKeyboardFocusWithin) CommitNew(); }), DispatcherPriority.Input);
            };
            _newBox = box;
            return box;
        }

        private static TextBox Detach(TextBox box)
        {
            var parent = box.Parent as Panel;
            if (parent != null) parent.Children.Remove(box);
            return box;
        }

        /// <summary>打っている名前で予定を足す（空ならやめる）。打っていなければ何もしない。</summary>
        private void CommitNew()
        {
            if (_newBox == null || !_newOn.HasValue) return;

            var title = ScheduleFile.OneLine(_newBox.Text);
            var date = _newOn.Value;
            _newBox = null;
            _newOn = null;

            if (title.Length > 0 && Sync())   // 足すのは、外で書き換わっていても読み直した中身に足せばよい
            {
                _selected = _book.Add(date, date, title);
                _selectedOriginal = null;
                Save();
            }
            Rebuild();
            Focus();
        }

        private void CancelNew()
        {
            if (_newBox == null) return;
            _newBox = null;
            _newOn = null;
            Rebuild();
            Focus();
        }

        // ---------------------------------------------------------------
        // アウトラインの見出しを落として登録する（MainWindow.Calendar.cs が、アウトラインのドラッグから呼ぶ）
        // ---------------------------------------------------------------

        /// <summary>アウトラインの見出しをつかんで、この位置（カレンダーの中の位置）に来た。日の上なら、その日を塗って true。</summary>
        public bool ShowDrop(Point point)
        {
            DateTime? date = null;
            if (!_editorOpen && point.X >= 0 && point.Y >= 0 && point.X <= ActualWidth && point.Y <= ActualHeight)
            {
                var inView = TranslatePoint(point, _scroll);
                if (inView.Y >= 0 && inView.Y <= _scroll.ViewportHeight) date = DateAt(TranslatePoint(point, _weeks));
            }

            if (date != _dropOn)
            {
                var old = _dropOn;
                _dropOn = date;
                if (old.HasValue && _cells.ContainsKey(old.Value)) _cells[old.Value].Background = CellFill(old.Value);
                if (date.HasValue && _cells.ContainsKey(date.Value)) _cells[date.Value].Background = CellFill(date.Value);
            }
            return date.HasValue;
        }

        /// <summary>
        /// 塗った日に、その見出しと結んだ1日の予定を足す（名前は見出しの文字）。塗った日が無ければ何もしない。
        /// file は保存してある文書（保存していない文書は、呼ぶ側で断る）。
        /// </summary>
        public void DropHeading(string file, string page, string heading)
        {
            var date = _dropOn;
            ClearDrop();
            if (!date.HasValue) return;
            CommitNew();
            if (!Sync()) return;

            var item = _book.Add(date.Value, date.Value, heading, file, page, heading);
            _selected = item;
            _selectedOriginal = null;
            Save();
            Rebuild();
            Focus();
            _status("見出しを予定にしました（何日かにするときは帯の端を引っぱる）: " + heading);
        }

        /// <summary>落とすのをやめた（塗った日を戻す）。</summary>
        public void ClearDrop()
        {
            ShowDrop(new Point(-1, -1));
        }

        // ---------------------------------------------------------------
        // キー
        // ---------------------------------------------------------------

        private void OnKey(object sender, KeyEventArgs e)
        {
            if (_editorOpen)
            {
                if (e.Key == Key.Escape)
                {
                    e.Handled = true;
                    CloseEditor();
                }
                return;
            }
            if (_newBox != null && _newBox.IsKeyboardFocusWithin) return;

            var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            switch (e.Key)
            {
                case Key.Delete:
                    if (_selected == null) return;
                    e.Handled = true;
                    Delete(_selected, _selectedOriginal);   // 繰り返しの予定は、選んだ回だけ
                    break;
                case Key.Z:
                    if (!ctrl) return;
                    e.Handled = true;
                    Undo();
                    break;
                case Key.PageUp:
                    e.Handled = true;
                    _scroll.PageUp();
                    break;
                case Key.PageDown:
                    e.Handled = true;
                    _scroll.PageDown();
                    break;
                case Key.Up:
                    e.Handled = true;
                    _scroll.LineUp();
                    break;
                case Key.Down:
                    e.Handled = true;
                    _scroll.LineDown();
                    break;
                case Key.Home:
                    e.Handled = true;
                    ScrollToToday();
                    break;
                case Key.Escape:
                    // 選んでいる予定・日があれば、選ぶのをやめる（カレンダーは閉じない。閉じるのは本文の左下のカレンダーのつまみ・Alt+C など。SPEC 22章）
                    if (_selected == null && !_dayAnchor.HasValue) return;
                    e.Handled = true;
                    ClearDays();
                    if (_selected != null)
                    {
                        _selected = null;
                        Rebuild();
                    }
                    break;
            }
        }

        /// <summary>予定を消す。繰り返しの予定で original（その回の決まりどおりの日）があれば、その回だけ消す。</summary>
        private void Delete(ScheduleItem item, DateTime? original)
        {
            var key = ScheduleBook.KeyOf(item);
            if (!Sync()) return;
            var target = _book.Find(item, key);
            if (target == null)
            {
                Gone();
                return;
            }

            var one = target.IsRepeat && original.HasValue;
            if (!_book.Delete(target, original)) return;
            Save();
            if (ReferenceEquals(_selected, target)) _selected = null;
            Rebuild();
            _status(one
                ? "この回だけ消しました（Ctrl+Z で戻せます）: " + original.Value.ToString("M/d", CultureInfo.InvariantCulture) + " " + target.Title
                : "予定を消しました（Ctrl+Z で戻せます）: " + target.Title);
        }

        private void Undo()
        {
            if (_book.UndoCount == 0)
            {
                _status("戻せる操作がありません");
                return;
            }
            if (!Sync()) return;
            if (!_book.Undo())
            {
                _status("予定が外で書き換えられていたので、戻せる操作はありません");   // 読み直すと戻す記録は捨てる
                return;
            }

            _selected = null;
            Save();
            Rebuild();
            _status("戻しました");
        }
    }
}
