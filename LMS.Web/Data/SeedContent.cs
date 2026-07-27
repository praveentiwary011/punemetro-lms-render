namespace LMS.Web.Data;

/// <summary>Hand-authored, Pune Metro–specific course content used by the seeder.</summary>
public static class SeedContent
{
    public record CourseContent(
        string Module1, string[] Lessons1,
        string Module2, string[] Lessons2,
        string Question, string Answer, string[] Wrong,
        string? AssignmentTitle = null, string? AssignmentBrief = null);

    public static readonly Dictionary<string, CourseContent> Courses = new()
    {
        ["OPS101"] = new(
            "Network & Organisation",
            new[] { "Welcome & How Pune Metro Runs", "Purple & Aqua Line Familiarisation", "OCC, Stations & Duty Roles" },
            "Train Operating Procedures",
            new[] { "ATO/ATP Operating Modes", "Depot Departure & Mainline Entry", "Degraded Mode & Single-Line Working" },
            "At which station do the Purple and Aqua lines interchange?",
            "District Court", new[] { "Swargate", "Vanaz", "PCMC" },
            "Duty Handover Report",
            "Draft a complete end-of-shift handover for a Train Operator ending duty at Swargate: rolling stock status, incidents, speed restrictions and pending OCC instructions."),

        ["OPS210"] = new(
            "OCC Fundamentals",
            new[] { "Control Centre Layout & Desks", "Timetable & Headway Regulation", "Communication Protocols with Crew" },
            "Disruption Management",
            new[] { "Service Regulation Strategies", "Turnback & Short-Loop Working", "Incident Command & Logging" },
            "What is the primary aim of headway regulation?",
            "Maintain even spacing between trains", new[] { "Achieve maximum top speed", "Reduce station dwell to zero", "Minimise crew changeovers" }),

        ["OPS220"] = new(
            "Depot Layout & Safety",
            new[] { "Depot Zones & Movement Rules", "Wake-up & Sleep Tests", "Shunting Signals & Speed Limits" },
            "Stabling Operations",
            new[] { "Stabling Plans & Berthing", "Washing & Inspection Lines", "Handover to Mainline Service" },
            "Before mainline entry every trainset must complete:",
            "A wake-up test", new[] { "A brake overhaul", "Wheel re-profiling", "A paint inspection" },
            "Depot Movement Risk Assessment",
            "Identify five movement risks in the stabling yard during night hours and propose mitigations aligned with depot SOPs."),

        ["STN201"] = new(
            "Station Systems",
            new[] { "AFC Gates & Ticketing Media", "PA, PIDS & CCTV Operation", "Station SCADA & Utilities Basics" },
            "Passenger Handling",
            new[] { "Peak-Hour Crowd Management", "Assisting Divyangjan & Senior Citizens", "Lost & Found and Complaint Handling" },
            "Which card standard is accepted at Pune Metro AFC gates?",
            "NCMC (National Common Mobility Card)", new[] { "Magnetic stripe tokens only", "Paper tickets only", "Proprietary city card only" }),

        ["STN230"] = new(
            "Crowd Science Basics",
            new[] { "Flow, Density & Pinch Points", "Queuing Layouts that Work", "Signage & Wayfinding under Load" },
            "Event Day Playbooks",
            new[] { "Stadium & Festival Surge Plans", "Coordination with Police & Security", "Post-Event Debrief & Metrics" },
            "What is the first action when platform density becomes unsafe?",
            "Regulate inflow at the gates", new[] { "Speed up train departures", "Switch off escalators", "Make no announcements to avoid panic" },
            "Event-Day Crowd Plan",
            "Prepare a one-page crowd management plan for your station for an IPL match day: inflow control, queuing, announcements and staffing."),

        ["SAF110"] = new(
            "Emergency Fundamentals",
            new[] { "Fire Detection & Alarm Zones", "Evacuation Routes & Assembly Points", "Incident Command Roles at Stations" },
            "Traction & Electrical Safety",
            new[] { "25kV OHE Safe Working Distances", "Power Block Request Procedure", "Emergency Trip & Incident Reporting" },
            "Who acts as incident commander at a station until relieved?",
            "The Station Controller", new[] { "The newest security guard", "Any passenger volunteer", "The ticket vendor" }),

        ["SAF150"] = new(
            "Fire Science & Systems",
            new[] { "Fire Triangle & Fire Classes", "Detection & Suppression Systems", "Choosing the Right Extinguisher" },
            "Drills & Evacuation",
            new[] { "Planning a Station Drill", "Conducting & Marshalling", "Observations, Findings & Reporting" },
            "Which extinguisher is correct for a live electrical panel fire?",
            "CO2 extinguisher", new[] { "Water jet", "Foam", "Wet chemical" },
            "Station Fire Drill Plan",
            "Design a 30-minute evacuation drill for your station: objectives, roles, timeline, safety controls and observer checklist."),

        ["SAF201"] = new(
            "First Response",
            new[] { "Scene Safety & Primary Survey", "CPR & AED Operation", "Bleeding, Burns & Fractures" },
            "Metro-Specific Scenarios",
            new[] { "Platform & Track-Side Casualties", "Heat Exhaustion & Crowd Illness", "Handover to Ambulance / EMS" },
            "What is the correct chest-compression rate for adult CPR?",
            "100–120 per minute", new[] { "40–60 per minute", "About 200 per minute", "As fast as possible without rhythm" }),

        ["ENG150"] = new(
            "Trainset Familiarisation",
            new[] { "3-Car Formation & Key Subsystems", "Daily Inspection Walk-around", "Cab Equipment & Isolation Switches" },
            "Bogies & Brakes",
            new[] { "Bogie Components & Suspension", "Friction & Regenerative Braking", "Common Faults & How to Report Them" },
            "Pune Metro trainset car bodies are made primarily of:",
            "Aluminium", new[] { "Cast iron", "Timber composite", "Stainless steel only" },
            "Daily Inspection Checklist Exercise",
            "Complete a simulated daily inspection walk-around and submit your filled checklist with any defects you would log."),

        ["ENG240"] = new(
            "Track Basics",
            new[] { "Track Geometry Parameters", "Rail Fastenings & Welds", "Points, Crossings & Lubrication" },
            "Civil Structures",
            new[] { "Viaduct & Bearing Inspection", "Drainage, Corrosion & Vegetation", "Track Access & Protection Rules" },
            "Which gauge does Pune Metro run on?",
            "Standard gauge (1435 mm)", new[] { "Broad gauge (1676 mm)", "Metre gauge", "Narrow gauge" }),

        ["ENG310"] = new(
            "Saloon HVAC",
            new[] { "HVAC Layout & Refrigeration Cycle", "Filters, Hygiene & Air Quality", "Temperature Control Logic" },
            "Auxiliary Systems",
            new[] { "Auxiliary Converter Overview", "Battery & Emergency Supply", "Reading Fault Codes & Diagnostics" },
            "During a total power failure, saloon emergency lighting runs on:",
            "The battery system", new[] { "The 25kV OHE directly", "A diesel generator in each car", "Solar panels on the roof" },
            "HVAC Fault Diagnosis Report",
            "Given the symptom log provided in class, identify the two most likely HVAC faults and describe your diagnostic sequence."),

        ["SIG110"] = new(
            "CBTC Principles",
            new[] { "Moving Block vs Fixed Block", "Movement Authority Explained", "Onboard & Wayside Equipment" },
            "Failure Modes",
            new[] { "Degraded Signalling Modes", "Restricted Manual Driving Rules", "Reporting & Recovery Sequence" },
            "In CBTC, a train's position is reported primarily by:",
            "The train's onboard equipment", new[] { "Track circuits alone", "Station staff observation", "GPS satellites only" }),

        ["SIG205"] = new(
            "Telecom Backbone",
            new[] { "Fibre Ring & Network Topology", "Train Radio (TETRA) Operation", "Master Clock & Public Address" },
            "Passenger Information Systems",
            new[] { "PIDS Content Management", "CCTV Operations & Privacy Rules", "Fault Logging & Escalation" },
            "Which system carries driver–OCC voice communication?",
            "Train radio (TETRA)", new[] { "Public WiFi", "The PIDS displays", "Station landlines only" },
            "PIDS Disruption Message Pack",
            "Write PIDS + PA message sets (Marathi/Hindi/English) for a 15-minute Aqua Line delay, an escalator outage, and a platform change."),

        ["SIG260"] = new(
            "AFC Architecture",
            new[] { "Gates, Validators & TVMs", "NCMC & QR Ticketing Flows", "Central Computer & Settlement" },
            "Maintenance Practice",
            new[] { "Preventive Maintenance Schedule", "Top 10 Gate Faults & First Aid", "Revenue Data Handling & Audit" },
            "NCMC stands for:",
            "National Common Mobility Card", new[] { "New City Metro Card", "National Cash Machine Card", "Network Control Master Card" }),

        ["CUS140"] = new(
            "Accessibility Infrastructure",
            new[] { "Lifts, Ramps & Tactile Paths", "Reserved Spaces & Signage", "Wheelchair Handling Technique" },
            "Assistance Protocol",
            new[] { "Offering Help the Right Way", "Communication Etiquette", "Emergency Evacuation of PwD" },
            "What is the correct first step when assisting a visually-impaired passenger?",
            "Ask how they would like to be assisted", new[] { "Take their arm and lead them", "Speak loudly and slowly", "Call security immediately" },
            "Accessibility Audit of Your Station",
            "Walk your station as a wheelchair user would: list barriers found from entry to platform and propose fixes with photos."),

        ["CUS180"] = new(
            "Announcement Craft",
            new[] { "Voice, Pace & Clarity", "Trilingual Announcement Templates", "Announcing Disruptions Calmly" },
            "Difficult Conversations",
            new[] { "Service-Recovery Phrasing", "Handling Anger Without Escalating", "When and How to Escalate" },
            "What is the announcement language order at Pune Metro?",
            "Marathi, Hindi, then English", new[] { "English only", "Hindi, English, Marathi", "Any order the announcer prefers" }),

        ["LDR120"] = new(
            "Leading Frontline Teams",
            new[] { "The Supervisor's Role at Pune Metro", "Running Effective Shift Briefings", "Delegation & Follow-up" },
            "People Conversations",
            new[] { "Giving Feedback that Lands", "Performance Conversations", "Recognising & Motivating the Team" },
            "An effective shift briefing should be:",
            "Short, structured and safety-first", new[] { "As long as possible", "Optional for experienced staff", "Focused only on discipline issues" },
            "Shift Briefing Script",
            "Write the full script of a 10-minute morning shift briefing for your team, covering safety notice, service changes, staffing and one improvement focus."),

        ["LDR210"] = new(
            "Understanding Conflict",
            new[] { "Sources of Team Conflict", "Active Listening & De-escalation", "Cross-Department Friction Points" },
            "Resolution Practice",
            new[] { "A Simple Mediation Framework", "Difficult Colleague Scenarios", "Building Team Working Agreements" },
            "What is the first step in de-escalating a heated disagreement?",
            "Listen actively without interrupting", new[] { "Raise your voice to take control", "Immediately assign blame", "Send everyone home" }),

        ["ONB100"] = new(
            "Welcome to Pune Metro",
            new[] { "Organisation, Vision & Network", "Code of Conduct Essentials", "Our Service Values" },
            "Working Here",
            new[] { "HR Policies You Must Know", "Safety Culture from Day One", "Growth, Training & Career Paths" },
            "Pune Metro is built and operated by:",
            "Maha-Metro (Maharashtra Metro Rail Corporation)", new[] { "Indian Railways directly", "A private bus operator", "The airport authority" },
            "My First 30 Days Reflection",
            "Describe three things that surprised you positively in your first weeks, one process you found confusing, and one improvement idea."),

        ["ONB110"] = new(
            "Your Digital Toolkit",
            new[] { "Email, HRMS & Single Sign-on", "Navigating the LMS", "IT Security Basics & Passwords" },
            "Working Smart",
            new[] { "Raising IT Tickets Properly", "Data Protection Do's & Don'ts", "E-Office Approvals Workflow" },
            "Where do you complete mandatory e-learning modules?",
            "The Pune Metro LMS", new[] { "Any public video site", "Personal email", "The canteen notice board" })
    };

    public static readonly (string Title, string Body, string Reply)[] Threads =
    {
        ("Speed limit in restricted manual mode?", "Module 2 mentions RM mode but not the exact speed. Is depot RM different from mainline?", "25 km/h on the mainline; depot movements are limited further — see the depot SOP annexure."),
        ("AFC gate shows 'card blocked' for valid NCMC", "Had three passengers today with valid cards rejected at gate 4. Workaround?", "Direct them to the TVM for a status check and log gate + card BIN in the fault register; engineering is tracking this batch."),
        ("Best way to memorise station sequence?", "New joiner here — any tricks for learning both corridors quickly?", "Print the line diagram and quiz yourself on interchanges first; the OPS101 lesson has a mnemonic that helps."),
        ("Evacuation drill timing", "Is the drill timed from alarm to platform-clear or to assembly-point count?", "From alarm activation to headcount complete at the assembly point — both timestamps go in the report."),
        ("Wheelchair ramp at older entrance", "Gate 2 ramp gradient feels too steep for solo wheelchair users. Who do I flag this to?", "Raise it via Support → HR/Learning and copy your Station Controller; accessibility audits feed the retrofit list."),
        ("Regenerative braking question", "Does regen braking work when the OHE section is isolated?", "No — with the section isolated the energy has nowhere to go; blended friction braking takes over automatically."),
        ("PIDS shows wrong destination", "Yesterday PIDS displayed Vanaz for a Ramwadi train for ~2 minutes. Passengers were confused.", "Log it with exact time in the telecom fault register; there was a content-sync fault that evening, already patched."),
        ("Handling festival crowd at District Court", "Any playbook for interchange crush during Ganesh visarjan week?", "Yes — STN230 module 2 has the surge plan; also coordinate marshal positions with security the day before."),
        ("CPR refresher frequency", "How often should station staff refresh CPR certification?", "Annually via SAF201 or the Red Cross partner course; your tracker shows the due date."),
        ("Quiz attempt froze", "My checkpoint quiz timer froze at 4:32 and auto-submitted. Can I get a retake?", "Yes — raise it under Assessments → Retake Assessments citing the freeze; approvals add one attempt."),
        ("Shift briefing template", "Does anyone have the one-page briefing template from LDR120?", "It's in Knowledge Hub → Documents under 'Shift Handover Procedure'; the LDR120 assignment uses the same structure."),
        ("Points failure procedure", "If points fail at a terminal station, who authorises manual operation?", "Only OCC authorises clamping and manual operation; never touch point machines without a power block confirmation.")
    };

    public static readonly (string Subject, string Body)[] Messages =
    {
        ("Welcome to the course", "Welcome aboard! Start with Module 1 and bring questions to the next doubt-clearing session. The checkpoint quiz opens after both modules."),
        ("Doubt on module 2", "In lesson 2 you mentioned the depot limit — does the same apply during washing-line movements? Wanted to confirm before my assessment."),
        ("Assignment format query", "Is a bulleted checklist acceptable for the field report, or do you want full paragraphs? Also, is there a page limit?"),
        ("Session reschedule", "Tomorrow's classroom session moves from 10:00 to 14:00 — same venue. Attendance will still be marked; reply if you have a duty clash."),
        ("Great progress this week", "You've cleared both modules and your practice scores are consistently above passing. Attempt the checkpoint this week while it's fresh."),
        ("Quiz revision tips", "Focus on the procedures lesson and the do's-and-don'ts list; two of the three questions come from those areas."),
        ("Attendance correction", "You were marked absent on Tuesday but you attended the second half. I've corrected it to 'Late' — check your progress report."),
        ("Certificate query", "My final grade shows 82% but the certificate hasn't appeared in my tracker. Could you check if it needs re-issuing?")
    };

    public static readonly string[] SessionTitles =
    {
        "Instructor-led classroom session", "Doubt-clearing & revision", "Hands-on practice workshop",
        "Scenario walkthrough & role play", "Assessment preparation clinic", "Field visit briefing"
    };

    public static readonly (string Title, string Body)[] CourseAnnouncements =
    {
        ("Assignment deadline reminder", "The field assignment closes this weekend — submissions after the deadline need trainer approval."),
        ("Extra session added", "By popular demand an extra doubt-clearing session is scheduled; see Training Calendar for the slot."),
        ("Quiz window open", "The checkpoint quiz is now open. You have two attempts; review both modules before you start."),
        ("Reading material updated", "Lesson notes were updated with the latest SOP revision — please re-read the procedures lesson."),
        ("Well done, batch!", "Average scores this month are the best so far — keep it up, and post your doubts in the forum.")
    };
}
