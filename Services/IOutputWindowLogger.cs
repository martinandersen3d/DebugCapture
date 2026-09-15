using System;
using System.Threading;
using System.Threading.Tasks;

namespace DebugCapture.Services;

internal interface IOutputWindowLogger
{
    Task LogAsync(Exception exception, CancellationToken cancellationToken = default);

    Task LogAsync(string message, CancellationToken cancellationToken = default);
}
