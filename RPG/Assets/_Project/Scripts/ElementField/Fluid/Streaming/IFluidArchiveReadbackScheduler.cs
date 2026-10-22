using System;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace Game.ElementField
{
    /// <summary>
    /// 隔离 Unity 静态 AsyncGPUReadback API，使 Streaming Runtime 能用 Fake Scheduler 验证
    /// callback 只登记、下一次 Update 才提交权威的时序。
    /// </summary>
    internal interface IFluidArchiveReadbackScheduler
    {
        bool IsPending { get; }
        void Request(
            ref NativeArray<FluidGpuArchiveSample> destination,
            GraphicsBuffer source,
            Action<bool> completed);
        void WaitForCompletion();
    }

    internal sealed class UnityFluidArchiveReadbackScheduler : IFluidArchiveReadbackScheduler
    {
        private AsyncGPUReadbackRequest _request;
        private Action<bool> _completed;
        private readonly Action<AsyncGPUReadbackRequest> _unityCallback;

        public bool IsPending { get; private set; }

        public UnityFluidArchiveReadbackScheduler()
        {
            // Delegate 在初始化边界缓存；每次归档不创建 closure。
            _unityCallback = OnCompleted;
        }

        public void Request(
            ref NativeArray<FluidGpuArchiveSample> destination,
            GraphicsBuffer source,
            Action<bool> completed)
        {
            if (IsPending) throw new InvalidOperationException("Fluid archive readback is already pending.");
            _completed = completed ?? throw new ArgumentNullException(nameof(completed));
            IsPending = true;
            try
            {
                _request = AsyncGPUReadback.RequestIntoNativeArray(
                    ref destination, source, _unityCallback);
            }
            catch
            {
                IsPending = false;
                _completed = null;
                throw;
            }
        }

        public void WaitForCompletion()
        {
            if (IsPending && !_request.done)
                _request.WaitForCompletion();
        }

        private void OnCompleted(AsyncGPUReadbackRequest request)
        {
            Action<bool> callback = _completed;
            _completed = null;
            IsPending = false;
            callback?.Invoke(request.hasError);
        }
    }

    internal interface IFluidRestoreStatusReadbackScheduler
    {
        bool IsPending { get; }
        void Request(
            ref NativeArray<FluidGpuTransferStatus> destination,
            GraphicsBuffer source,
            Action<bool> completed);
        void WaitForCompletion();
    }

    internal sealed class UnityFluidRestoreStatusReadbackScheduler : IFluidRestoreStatusReadbackScheduler
    {
        private AsyncGPUReadbackRequest _request;
        private Action<bool> _completed;
        private readonly Action<AsyncGPUReadbackRequest> _unityCallback;
        public bool IsPending { get; private set; }

        public UnityFluidRestoreStatusReadbackScheduler() { _unityCallback = OnCompleted; }

        public void Request(
            ref NativeArray<FluidGpuTransferStatus> destination,
            GraphicsBuffer source,
            Action<bool> completed)
        {
            if (IsPending) throw new InvalidOperationException("Fluid restore status readback is already pending.");
            _completed = completed ?? throw new ArgumentNullException(nameof(completed));
            IsPending = true;
            try { _request = AsyncGPUReadback.RequestIntoNativeArray(ref destination, source, _unityCallback); }
            catch { IsPending = false; _completed = null; throw; }
        }

        public void WaitForCompletion()
        {
            if (IsPending && !_request.done) _request.WaitForCompletion();
        }

        private void OnCompleted(AsyncGPUReadbackRequest request)
        {
            Action<bool> callback = _completed;
            _completed = null;
            IsPending = false;
            callback?.Invoke(request.hasError);
        }
    }
}
