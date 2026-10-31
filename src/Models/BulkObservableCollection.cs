using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace DashFile.Models;

/// <summary>
/// 支持"整批替换"的集合：内容换掉时只发一次 Reset 通知。
///
/// 为什么需要它：直接 <c>Clear()</c> + 循环 <c>Add()</c> 时，
///   ListBox 每收到一次 Add 就会重新度量/布局一遍。一个 800 个文件的目录
///   就是 800 次布局 —— 这就是"切目录要顿零点几秒"的主因，跟 adb 快慢无关。
///   换成一次 Reset 后，布局只跑一次。
/// </summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    private bool _suppress;

    /// <summary>用新内容整体替换（只触发一次 Reset）</summary>
    public void ReplaceAll(IEnumerable<T> items)
    {
        _suppress = true;
        try
        {
            Items.Clear();
            foreach (var item in items) Items.Add(item);
        }
        finally
        {
            _suppress = false;
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    /// <summary>
    /// 把内容增量改造成 desired：按"引用身份"做最小改动（增/删/移动），
    /// 已经存在的对象一个都不重新创建，界面上的行容器也就不会被重建。
    ///
    /// 这就是目录树不卡的关键：以前每改一个展开状态就 <see cref="ReplaceAll"/>
    ///   一次（一次 Reset = 整棵树的可视元素推倒重来，200 行要 30~40ms）；
    ///   现在只对真正变动的行发一条事件，通常 0 条。
    ///
    /// 变化太大（首屏、换根）时退回整体 Reset，避免 O(n²) 的插入/移动。
    /// </summary>
    public void Patch(IReadOnlyList<T> desired)
    {
        if (desired.Count == 0)
        {
            if (Count > 0) ReplaceAll(desired);
            return;
        }

        // 先看有多少旧行能留下来：留得太少就不值得逐条改
        var want = new HashSet<T>(desired);
        var keep = 0;
        foreach (var item in this)
            if (want.Contains(item)) keep++;

        if (keep * 2 < desired.Count)
        {
            ReplaceAll(desired);
            return;
        }

        // 1) 删掉不在新列表里的
        for (var i = Count - 1; i >= 0; i--)
            if (!want.Contains(this[i])) RemoveAt(i);

        // 2) 按顺序落位：存在的移动，不存在的插入
        for (var i = 0; i < desired.Count; i++)
        {
            if (i < Count && ReferenceEquals(this[i], desired[i])) continue;

            var existing = IndexOf(desired[i]);
            if (existing < 0) Insert(i, desired[i]);
            else if (existing != i) Move(existing, i);
        }

        // 3) 兜底：绝不允许残留（之前用前后缀比对时就是因为残留多出旧行）
        while (Count > desired.Count) RemoveAt(Count - 1);
    }

    /// <summary>
    /// 把内容增量同步成 items：只对真正变化的中段发 Remove / Insert，
    /// 前后没变的部分一个事件都不发（ItemsControl 也就不会重建它们的可视元素）。
    /// </summary>
    public void SyncTo(IList<T> items)
    {
        var max = Math.Min(Count, items.Count);

        var prefix = 0;
        while (prefix < max && EqualityComparer<T>.Default.Equals(this[prefix], items[prefix])) prefix++;

        var suffix = 0;
        while (suffix < max - prefix &&
               EqualityComparer<T>.Default.Equals(this[Count - 1 - suffix], items[items.Count - 1 - suffix]))
            suffix++;

        for (var k = 0; k < Count - prefix - suffix; k++) RemoveAt(prefix);
        for (var k = 0; k < items.Count - prefix - suffix; k++) Insert(prefix + k, items[prefix + k]);
    }

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (!_suppress) base.OnCollectionChanged(e);
    }
}
