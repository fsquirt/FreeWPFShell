using System;
using Microsoft.UI.Dispatching;

namespace YouShell.Core
{
    /// <summary>
    /// 全局 UI 线程调度器（WinUI 3 的 DispatcherQueue）。
    /// 替代 WPF 中 Application.Current.Dispatcher 的用法：后台线程需更新 UI 绑定时，
    /// 统一通过此类 marshal 到 UI 线程。应用启动时调用 Initialize 注入主线程 DispatcherQueue。
    /// </summary>
    public static class UiDispatcher
    {
        private static DispatcherQueue? _queue;

        public static void Initialize(DispatcherQueue queue) => _queue = queue;

        public static bool IsInitialized => _queue != null;

        /// <summary>始终在 UI 线程异步执行（fire-and-forget）。未初始化时直接同步执行。</summary>
        public static void Enqueue(Action action)
        {
            if (_queue == null) { action(); return; }
            _queue.TryEnqueue(() => action());
        }

        /// <summary>若已在 UI 线程则同步执行，否则排队到 UI 线程执行。</summary>
        public static void Run(Action action)
        {
            if (_queue == null || _queue.HasThreadAccess) { action(); return; }
            _queue.TryEnqueue(() => action());
        }
    }
}
