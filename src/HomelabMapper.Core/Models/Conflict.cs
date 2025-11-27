namespace HomelabMapper.Core.Models;

public enum ConflictType
{
    TypeMismatch,
    IpMismatch,
    UnverifiedEntity,
    DuplicateEntity
}

public class Conflict
{
    public string Ip { get; set; } = string.Empty;
    public ConflictType Type { get; set; }
    public List<Entity> InvolvedEntities { get; set; } = new();
    public string Description { get; set; } = string.Empty;
}
