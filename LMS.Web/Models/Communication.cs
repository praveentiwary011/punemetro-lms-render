using System.ComponentModel.DataAnnotations;

namespace LMS.Web.Models;

public class Announcement
{
    public int Id { get; set; }
    public int? CourseId { get; set; }   // null = site-wide
    public Course? Course { get; set; }
    public string AuthorId { get; set; } = "";
    public ApplicationUser? Author { get; set; }
    [Required, MaxLength(200)]
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class DiscussionThread
{
    public int Id { get; set; }
    public int CourseId { get; set; }
    public Course? Course { get; set; }
    public string AuthorId { get; set; } = "";
    public ApplicationUser? Author { get; set; }
    [Required, MaxLength(200)]
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public bool IsPinned { get; set; }
    public bool IsLocked { get; set; }
    public ICollection<DiscussionPost> Posts { get; set; } = new List<DiscussionPost>();
}

public class DiscussionPost
{
    public int Id { get; set; }
    public int ThreadId { get; set; }
    public DiscussionThread? Thread { get; set; }
    public string AuthorId { get; set; } = "";
    public ApplicationUser? Author { get; set; }
    public string Body { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class Message
{
    public int Id { get; set; }
    public string SenderId { get; set; } = "";
    public ApplicationUser? Sender { get; set; }
    public string RecipientId { get; set; } = "";
    public ApplicationUser? Recipient { get; set; }
    [MaxLength(200)]
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    public DateTime SentAt { get; set; } = DateTime.UtcNow;
    public DateTime? ReadAt { get; set; }
}

public class Notification
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public ApplicationUser? User { get; set; }
    public string Title { get; set; } = "";
    public string? Link { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ReadAt { get; set; }
}
