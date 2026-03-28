using System.ComponentModel.DataAnnotations;
using TransactionDispatch.Application.Validation;

namespace TransactionDispatch.Application;

public sealed class DispatchRequest
{
    [Required(ErrorMessage = "folderPath is required.")]
    [NotWhiteSpace(ErrorMessage = "folderPath must not be empty or whitespace.")]
    public string FolderPath { get; set; } = string.Empty;

    public bool DeleteAfterSend { get; set; }

    /// <summary>Parameterless constructor for model binding.</summary>
    public DispatchRequest() { }

    /// <summary>Constructor for internal use (background service, tests).</summary>
    public DispatchRequest(string FolderPath, bool DeleteAfterSend = false)
    {
        this.FolderPath = FolderPath;
        this.DeleteAfterSend = DeleteAfterSend;
    }
}


