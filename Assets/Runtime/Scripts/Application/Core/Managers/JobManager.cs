using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Strands.Applications.Core
{
    /// <summary>
    /// Runs one slow piece of work at a time off the main thread, and hands the result back on it.
    /// The status bar reads <see cref="Status"/> and <see cref="Progress"/>.
    /// </summary>
    public class JobManager : MonoBehaviour
    {
        /// <summary>Progress when the work cannot say how far along it is.</summary>
        public const float Unknown = -1f;

        Task task;
        CancellationTokenSource cancel;
        Action completed;
        Action<Exception> failed;
        volatile float progress = Unknown;
        string idleMessage = "Ready";
        string status;

        public bool Busy { get { return task != null; } }
        public float Progress { get { return progress; } }
        public string Status { get { return Busy ? (Cancelling ? "Cancelling…" : status) : idleMessage; } }

        /// <summary>
        /// True from a press of Cancel until the work notices and stops, which is not at once: the analysis
        /// only looks between its stages.
        /// </summary>
        public bool Cancelling { get { return cancel != null && cancel.IsCancellationRequested; } }

        /// <summary>The message shown when nothing is running.</summary>
        public void SetIdleMessage(string message)
        {
            idleMessage = message;
        }

        /// <summary>
        /// Starts <paramref name="work"/> on a worker thread. Exactly one of <paramref name="done"/> and
        /// <paramref name="error"/> is then called on the main thread; a cancelled job calls neither.
        /// </summary>
        public void Run<T>(string statusText, Func<CancellationToken, IProgress<float>, T> work, Action<T> done, Action<Exception> error)
        {
            if (Busy)
                throw new InvalidOperationException("Another job is still running.");
            status = statusText;
            progress = Unknown;
            cancel = new CancellationTokenSource();
            CancellationToken token = cancel.Token;
            var reporter = new Reporter(this);
            Task<T> typed = Task.Run(() => work(token, reporter), token);
            task = typed;
            completed = () => done(typed.Result);
            failed = error;
        }

        public void Cancel()
        {
            if (cancel != null)
                cancel.Cancel();
        }

        void Update()
        {
            if (task == null || !task.IsCompleted)
                return;

            Task finished = task;
            Action onCompleted = completed;
            Action<Exception> onFailed = failed;
            bool wasCancelled = finished.IsCanceled || cancel.IsCancellationRequested;
            task = null;
            completed = null;
            failed = null;
            cancel.Dispose();
            cancel = null;

            if (wasCancelled)
                idleMessage = "Cancelled";
            else if (finished.IsFaulted)
                onFailed(finished.Exception.GetBaseException());
            else
                onCompleted();
        }

        void OnDestroy()
        {
            Cancel();
        }

        sealed class Reporter : IProgress<float>
        {
            readonly JobManager owner;
            public Reporter(JobManager owner) { this.owner = owner; }
            public void Report(float value) { owner.progress = value; }
        }
    }
}
