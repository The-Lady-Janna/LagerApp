using FluentValidation;
using Lager.Api.Controllers;
using Lager.Application.ImportExport;

namespace Lager.Api.Validation;

/// <summary>Gemeinsame Regeln der Import-/Export-Validatoren.</summary>
internal static class ImportExportRules
{
    public const string DelimiterMessage = "Das Trennzeichen muss 'semicolon' (;) oder 'comma' (,) sein.";

    public static IRuleBuilderOptions<T, string?> CsvDelimiter<T>(this IRuleBuilder<T, string?> rule) =>
        rule.Must(d => CsvFormat.TryParse(d, out _)).WithMessage(DelimiterMessage);

    /// <summary>Zeitraum <c>from</c>/<c>to</c>: beide lesbar (Datum oder Zeitpunkt) und <c>from</c> vor <c>to</c>.</summary>
    public static void ValidRange<T>(this AbstractValidator<T> validator, Func<T, string?> from, Func<T, string?> to)
    {
        validator.RuleFor(x => x).Custom((query, context) =>
        {
            if (!ExportRange.TryParse(from(query), to(query), out _, out var error))
                context.AddFailure("Range", error!);
        });
    }
}

/// <summary>Upload eines Imports: Datei vorhanden, nicht leer und höchstens <see cref="ImportLimits.MaxFileBytes"/> (5 MB).</summary>
public class ImportUploadValidator : AbstractValidator<ImportUpload>
{
    public ImportUploadValidator()
    {
        RuleFor(x => x.File).NotNull().WithMessage("Es wurde keine Datei hochgeladen (Formularfeld 'file').");
        When(x => x.File is not null, () =>
        {
            RuleFor(x => x.File!.Length).GreaterThan(0).WithName("File").WithMessage("Die Datei ist leer.");
            RuleFor(x => x.File!.Length).LessThanOrEqualTo(ImportLimits.MaxFileBytes).WithName("File")
                .WithMessage("Die Datei ist größer als " + ImportLimits.MaxFileBytes / (1024 * 1024) + " MB. Bitte in mehrere Dateien aufteilen.");
        });
    }
}

public class ImportQueryValidator : AbstractValidator<ImportQuery>
{
    public ImportQueryValidator()
    {
        RuleFor(x => x.Delimiter).CsvDelimiter();
    }
}

public class ExportFormatQueryValidator : AbstractValidator<ExportFormatQuery>
{
    public ExportFormatQueryValidator()
    {
        RuleFor(x => x.Delimiter).CsvDelimiter();
    }
}

public class MovementExportQueryValidator : AbstractValidator<MovementExportQuery>
{
    public MovementExportQueryValidator()
    {
        RuleFor(x => x.Delimiter).CsvDelimiter();
        this.ValidRange(x => x.From, x => x.To);
    }
}

public class AuditExportQueryValidator : AbstractValidator<AuditExportQuery>
{
    public AuditExportQueryValidator()
    {
        RuleFor(x => x.Delimiter).CsvDelimiter();
        RuleFor(x => x.User).OptionalText(ValidationLimits.MaxLabelLength);
        this.ValidRange(x => x.From, x => x.To);
    }
}
