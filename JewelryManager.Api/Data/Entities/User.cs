namespace JewelryManager.Api.Data.Entities;

public class User
{
    public Guid Id { get; set; }

    // Nullable: a business owner can pre-create an employee record by email
    // before that person ever signs in with Firebase.
    // The FirebaseUid gets linked on their first successful login.
    public Guid? BusinessId { get; set; }
    public Business? Business { get; set; }

    public string? FirebaseUid { get; set; }
    public string Email { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string? Phone { get; set; }
    public Role Role { get; set; } = Role.Owner;

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
