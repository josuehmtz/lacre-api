using Microsoft.AspNetCore.Identity;

namespace Invitations.Api.Features.Auth;

/// <summary>Mensajes de Identity (registro, contraseña) en español; por defecto vienen en inglés.</summary>
public class SpanishIdentityErrors : IdentityErrorDescriber
{
    private const string AccountExists = "Ya existe una cuenta con este correo. Inicia sesión o usa \"Continuar con Google\".";

    private static IdentityError Error(string code, string description) => new() { Code = code, Description = description };

    public override IdentityError DefaultError() => Error(nameof(DefaultError), "Ocurrió un error. Intenta de nuevo.");
    public override IdentityError ConcurrencyFailure() => Error(nameof(ConcurrencyFailure), "Alguien más modificó estos datos. Recarga e intenta de nuevo.");
    public override IdentityError PasswordMismatch() => Error(nameof(PasswordMismatch), "La contraseña es incorrecta.");
    public override IdentityError InvalidToken() => Error(nameof(InvalidToken), "El enlace ya no es válido.");
    public override IdentityError LoginAlreadyAssociated() => Error(nameof(LoginAlreadyAssociated), "Esta cuenta de Google ya está vinculada a otro usuario.");
    public override IdentityError InvalidUserName(string? userName) => Error(nameof(InvalidUserName), "El correo no es válido.");
    public override IdentityError InvalidEmail(string? email) => Error(nameof(InvalidEmail), "El correo no es válido.");
    // Usuario y correo son lo mismo: ambos duplicados muestran el mismo mensaje (el front quita repetidos).
    public override IdentityError DuplicateUserName(string userName) => Error(nameof(DuplicateUserName), AccountExists);
    public override IdentityError DuplicateEmail(string email) => Error(nameof(DuplicateEmail), AccountExists);
    public override IdentityError UserAlreadyHasPassword() => Error(nameof(UserAlreadyHasPassword), "Tu cuenta ya tiene contraseña.");
    public override IdentityError UserLockoutNotEnabled() => Error(nameof(UserLockoutNotEnabled), "El bloqueo no está habilitado para esta cuenta.");
    public override IdentityError PasswordTooShort(int length) => Error(nameof(PasswordTooShort), $"La contraseña debe tener al menos {length} caracteres.");
    public override IdentityError PasswordRequiresUniqueChars(int uniqueChars) => Error(nameof(PasswordRequiresUniqueChars), $"La contraseña debe tener al menos {uniqueChars} caracteres distintos.");
    public override IdentityError PasswordRequiresNonAlphanumeric() => Error(nameof(PasswordRequiresNonAlphanumeric), "La contraseña debe incluir al menos un símbolo.");
    public override IdentityError PasswordRequiresDigit() => Error(nameof(PasswordRequiresDigit), "La contraseña debe incluir al menos un número.");
    public override IdentityError PasswordRequiresLower() => Error(nameof(PasswordRequiresLower), "La contraseña debe incluir al menos una minúscula.");
    public override IdentityError PasswordRequiresUpper() => Error(nameof(PasswordRequiresUpper), "La contraseña debe incluir al menos una mayúscula.");
}
