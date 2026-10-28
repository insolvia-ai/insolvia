using System.Collections.Frozen;
using Insolvia.Core.Filings;

namespace Insolvia.Filing;

/// <summary>One stop's title and the attorney's next step.</summary>
/// <param name="Title">What happened.</param>
/// <param name="Action">What to do.</param>
public sealed record StopReason(string Title, string Action);

/// <summary>
/// Why a filing stopped, and what the attorney does next — in words (from
/// <c>core/hand_back.py</c>). Every reason the worker or a driver can stop
/// for is a key here (HandBackTests reads the sources), so a record never
/// carries a reason nobody wrote an instruction for. Two families:
/// HAND-BACK (before the final submit — nothing reached the court) and
/// OUTCOME-UNKNOWN (after the mark — reconcile first, never retried).
/// </summary>
public static class HandBackReasons
{
    private const string FromChecklist =
        " Nothing reached the court. File the case from the packet screen's checklist in your own CM/ECF session, then record the court's case number on this filing — or record that you have not filed it, which lets you approve a new filing.";

    private const string Reconcile =
        " Look the debtor up on the court's own case query before doing anything else, and record what it shows on this filing: the case number if the case is there; that it is not, only once you have checked — that is what lets a new filing be approved.";

    /// <summary>The hand-back reasons.</summary>
    public static readonly FrozenDictionary<string, StopReason> HandBack = new Dictionary<string, StopReason>(StringComparer.Ordinal)
    {
        ["court_not_verified"] = new("Insolvia does not file in this court yet", "No filing driver has been verified on this court's training database, so Insolvia prepares the case and you file it." + FromChecklist),
        ["host_not_allowed"] = new("The court's address is outside what this environment may reach", "The worker refused before opening your login." + FromChecklist),
        ["submissions_disabled"] = new("Automated filing is switched off", "Insolvia's filing kill switch is off in this environment; nothing was opened or sent." + FromChecklist),
        ["approval_unavailable"] = new("The approval could not be used", "It had expired, been cancelled, been used, or the filing set changed after you approved it. Review the filing set and approve again, or file it yourself." + FromChecklist),
        ["credential_unavailable"] = new("Your court login could not be used", "It was revoked, or your filing authorization was withdrawn, before the filing went in." + FromChecklist),
        ["outside_document_required"] = new("A document prepared outside Insolvia must go in with the petition", "The court takes it as part of the opening filing and Insolvia does not hold it." + FromChecklist),
        ["case_upload_invalid"] = new("The Case Upload file could not be built from this case", "Debtor.txt failed the court's specification (or a fact it needs — a tax id, the county, the division's office code — is missing), so the worker stopped before signing in. Nothing reached the court." + FromChecklist),
        ["document_mismatch"] = new("A stored document no longer matches what you approved", "Its bytes do not have the digest the approval was bound to; nothing was filed. Re-assemble the packet and review it again."),
        ["changed_after_approval"] = new("The filing set changed while it was being filed", "The worker re-checked the approval's digest immediately before the final submit, and the case no longer matches it. Review the filing set and approve again, or file it yourself." + FromChecklist),
        ["sign_in_failed"] = new("The court refused the sign-in", "Check your PACER login and password, update them in Insolvia, and approve again." + FromChecklist),
        ["mfa_rejected"] = new("The court refused the authenticator code", "Re-enrol your PACER authenticator seed in Insolvia, then approve again." + FromChecklist),
        ["unrecognised_screen"] = new("The court showed a screen Insolvia does not recognise", "The worker stopped rather than guess." + FromChecklist),
        ["court_message"] = new("The court stopped the filing with a message", "Read what the court said, below." + FromChecklist),
        ["duplicate_case"] = new("The court reports a possible duplicate case for this debtor", "Check the court's records for an existing case before filing anything." + FromChecklist),
        ["court_error"] = new("The court's system returned an error", "Nothing was submitted." + FromChecklist),
        ["court_timeout"] = new("The court did not answer in time", "The worker gave up before the final submit." + FromChecklist),
        ["upload_mismatch"] = new("The court's list of uploaded documents does not match the filing set", "The worker stopped before the final submit." + FromChecklist),
        ["interrupted"] = new("The filing run was interrupted before the final submit", "The worker stopped (a crash or a timeout) before anything was submitted, and a redelivered job never restarts a filing." + FromChecklist),
        ["worker_error"] = new("The filing worker hit an unexpected error before the final submit", "It stopped rather than retry." + FromChecklist),
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>The outcome-unknown reasons.</summary>
    public static readonly FrozenDictionary<string, StopReason> OutcomeUnknown = new Dictionary<string, StopReason>(StringComparer.Ordinal)
    {
        ["submit_timeout"] = new("The court did not answer the final submit", "The filing may or may not have been docketed." + Reconcile),
        ["submit_error"] = new("The final submit failed in a way that does not say what the court did", "The filing may or may not have been docketed." + Reconcile),
        ["receipt_missing"] = new("The court's confirmation had no case number", "The filing may have been docketed without a confirmation Insolvia could read." + Reconcile),
        ["unrecognised_confirmation"] = new("The court answered the final submit with a screen Insolvia does not recognise", "The filing may or may not have been docketed." + Reconcile),
        ["interrupted_after_submit_mark"] = new("The filing run stopped at the final submit", "The worker had marked the filing as about to submit, so it cannot know whether the court received it, and it never submits twice." + Reconcile),
        ["capture_interrupted"] = new("The court confirmed the filing, but the receipt could not be stored", "The case number below is the court's own." + Reconcile),
        ["case_not_recorded"] = new("The court confirmed the filing, but the case could not be marked filed", "The court's confirmation is stored with the filing, but its case number or date could not be read, or the case changed while it was being recorded. The case number below is the court's own." + Reconcile),
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>The stored note for a hand-back. An unknown reason is a programming error the catalogue test catches.</summary>
    public static HandBackNote HandBackNote(string reason, FilingState stage, string? courtSaid = null)
    {
        var entry = HandBack[reason];
        return new HandBackNote(reason, stage.ToWireState(), entry.Title, entry.Action, courtSaid);
    }

    /// <summary>The stored note for an outcome-unknown stop — the same shape, its action always to reconcile first.</summary>
    public static HandBackNote OutcomeUnknownNote(string reason, FilingState stage, string? courtSaid = null)
    {
        var entry = OutcomeUnknown[reason];
        return new HandBackNote(reason, stage.ToWireState(), entry.Title, entry.Action, courtSaid);
    }

    private static string ToWireState(this FilingState state) => Core.Wire.ToWire(state);
}
