using System.Threading.Tasks;

namespace DebugCapture.Services;

internal interface IDebuggerVariableExportService
{
    Task ExportAsync(CaptureFileSet fileSet);
}
