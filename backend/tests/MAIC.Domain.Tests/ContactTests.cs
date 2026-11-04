using MAIC.Domain.Contacts;
using Xunit;

namespace MAIC.Domain.Tests;

public sealed class ContactTests
{
    [Fact]
    public void Contact_owns_UUIDv7_identities_and_replacement_retains_unchanged_identity_ids()
    {
        var workspace = Guid.CreateVersion7();
        var contact = new Contact(workspace, "  Customer  ", [new(" CRM:EXTERNAL ", " Opaque@Value "), new("email", "Old")]);
        var retained = contact.Identities.Single(i => i.Kind == "crm:external");
        var contactId = contact.Id;
        var created = contact.CreatedAt;
        Assert.Equal("Customer", contact.DisplayName);
        Assert.Equal(" Opaque@Value ", retained.Value);
        Assert.Equal('7', contact.Id.ToString()[14]);
        Assert.All(contact.Identities, i =>
        {
            Assert.Equal('7', i.Id.ToString()[14]);
            Assert.Equal(workspace, i.WorkspaceId);
            Assert.Equal(contact.Id, i.ContactId);
        });
        contact.ReplaceDetails("Next", [new("CRM:external", " Opaque@Value "), new("email", "New")]);
        Assert.Equal(retained.Id, contact.Identities.Single(i => i.Kind == retained.Kind).Id);
        Assert.DoesNotContain(contact.Identities, i => i.Value == "Old");
        Assert.Equal(contactId, contact.Id);
        Assert.Equal(workspace, contact.WorkspaceId);
        Assert.Equal(created, contact.CreatedAt);
        contact.ReplaceDetails("Empty", []);
        Assert.Empty(contact.Identities);
    }

    [Fact]
    public void Invalid_replacements_are_rejected_without_changing_the_aggregate()
    {
        var contact = new Contact(Guid.NewGuid(), "Before", [new("email", "Case@Value")]);
        var original = Assert.Single(contact.Identities).Id;
        Assert.Throws<ArgumentException>(() => contact.ReplaceDetails("After", [new(" Email ", "same"), new("email", "same")]));
        Assert.Equal("Before", contact.DisplayName);
        Assert.Equal(original, Assert.Single(contact.Identities).Id);
        Assert.Throws<ArgumentException>(() => new Contact(Guid.Empty, "Name", []));
        Assert.False(Contact.IsValidDetails("Name", null));
        Assert.False(Contact.IsValidDetails("Name", [null!]));
        Assert.True(Contact.IsValidDetails("Name", [new("email", "case"), new("email", "Case"), new("email", " case ")]));
    }

    [Fact]
    public void Bounds_and_postgres_text_validation_apply_before_persistence()
    {
        foreach (var name in new string?[] { null, "", " ", new('x', 201), "bad\0name", "bad\nname", "\ud800", "\udfff" })
            Assert.False(Contact.IsValidDetails(name, []));
        foreach (var kind in new string?[] { null, "", " ", "9email", "has space", "é", "mail\t", new('a', 65) })
            Assert.False(Contact.IsValidDetails("Name", [new(kind, "value")]));
        foreach (var value in new string?[] { null, "", new('x', 513), "bad\0value", "bad\nvalue", "\ud800", "\udfff", "\u0085" })
            Assert.False(Contact.IsValidDetails("Name", [new("email", value)]));
        Assert.True(Contact.IsValidDetails(new('x', 200), [new(new('a', 64), new('v', 512))]));
        Assert.True(Contact.IsValidDetails("Customer 😀", [new("crm._:-123", "😀 exact ")]));
        Assert.True(Contact.IsValidDetails("Name", Enumerable.Range(0, 20).Select(i => new ContactIdentityValue("crm", i.ToString())).ToArray()));
        Assert.False(Contact.IsValidDetails("Name", Enumerable.Range(0, 21).Select(i => new ContactIdentityValue("crm", i.ToString())).ToArray()));
    }
}
