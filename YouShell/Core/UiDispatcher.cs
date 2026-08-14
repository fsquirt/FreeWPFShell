using System;
using System.Threading.Tasks;
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

        /// <summary>
        /// 在 UI 线程执行异步操作并返回其结果；非 UI 线程调用时 marshal 过去。
        /// 用于 ContentDialog 等必须在 UI 线程创建的 WinRT 对象（否则抛 0x8001010E）。
        /// </summary>
        public static Task<T> RunAsync<T>(Func<Task<T>> func)
        {
            if (_queue == null || _queue.HasThreadAccess) return func();

            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.TryEnqueue(() => _ = InvokeAsync(func, tcs));
            return tcs.Task;
        }

        private static async Task InvokeAsync<T>(Func<Task<T>> func, TaskCompletionSource<T> tcs)
        {
            try { tcs.TrySetResult(await func()); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        }
    }
}
