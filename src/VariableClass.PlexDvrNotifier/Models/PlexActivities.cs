using System.Runtime.Serialization;
using System.Xml.Serialization;

namespace VariableClass.PlexDvrNotifier.Models;

[XmlRoot("MediaContainer")]
public class PlexActivitiesResponse
{
  [XmlElement("Activity")]
  public required Activity[]? Activities { get; set; }
}

public class Activity
{
  private const string TypeRecording = "grabber.grab";
  private const string TitleRecording = "Recording";

  [XmlAttribute("type")]
  public required string Type { get; set; }
  
  [XmlAttribute("title")]
  public required string Title { get; set; }
  
  [XmlAttribute("subtitle")]
  public required string Subtitle { get; set; }

  public bool IsRecording()
    => Type == TypeRecording && Title == TitleRecording;
}
