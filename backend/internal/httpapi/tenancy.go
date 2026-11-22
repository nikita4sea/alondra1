package httpapi

import (
	"errors"
	"net/http"
	"time"

	"alondra1/backend/internal/auth"
	"alondra1/backend/internal/tenancy"
)

func (s *Server) tenancyRoutes(r *router) {
	r.handle("GET", "/v1/tenants", s.authenticated(s.listTenants))
	r.handle("POST", "/v1/tenants", s.authenticated(s.createTenant))
	r.handle("GET", "/v1/tenants/{id}", s.authenticated(s.getTenant))
	r.handle("PATCH", "/v1/tenants/{id}", s.authenticated(s.renameTenant))
	r.handle("DELETE", "/v1/tenants/{id}", s.authenticated(s.deleteTenant))
	r.handle("GET", "/v1/tenants/{id}/members", s.authenticated(s.listMembers))
	r.handle("PATCH", "/v1/tenants/{id}/members/{user_id}", s.authenticated(s.changeRole))
	r.handle("DELETE", "/v1/tenants/{id}/members/{user_id}", s.authenticated(s.removeMember))
	r.handle("GET", "/v1/tenants/{id}/invites", s.authenticated(s.listInvites))
	r.handle("POST", "/v1/tenants/{id}/invites", s.authenticated(s.createInvite))
	r.handle("DELETE", "/v1/tenants/{id}/invites/{invite_id}", s.authenticated(s.revokeInvite))
	r.handle("POST", "/v1/invites/{token}/accept", s.authenticated(s.acceptInvite))
	r.handle("GET", "/v1/devices", s.authenticated(s.listDevices))
	r.handle("DELETE", "/v1/devices/{id}", s.authenticated(s.revokeDevice))
	r.handle("DELETE", "/v1/me", s.authenticated(s.deleteAccount))
}

func tenantJSON(v tenancy.View) map[string]any {
	return map[string]any{"id": v.ID, "type": v.Type, "name": v.Name, "role": v.Role, "created_at": v.CreatedAt.UTC().Format(time.RFC3339)}
}

func (s *Server) listTenants(w http.ResponseWriter, r *http.Request, p auth.Principal) {
	views, err := s.Tenancy.List(r.Context(), p.UserID)
	if err != nil {
		s.tenancyError(w, r, err)
		return
	}
	items := make([]map[string]any, 0, len(views))
	for _, v := range views {
		items = append(items, tenantJSON(v))
	}
	writeJSON(w, http.StatusOK, map[string]any{"tenants": items})
}

type createTenantRequest struct {
	Type string `json:"type"`
	Name string `json:"name"`
}

func (s *Server) createTenant(w http.ResponseWriter, r *http.Request, p auth.Principal) {
	var body createTenantRequest
	if !decode(w, r, &body) {
		return
	}
	view, err := s.Tenancy.Create(r.Context(), p.UserID, body.Type, body.Name)
	if err != nil {
		s.tenancyError(w, r, err)
		return
	}
	writeJSON(w, http.StatusCreated, tenantJSON(view))
}

func (s *Server) getTenant(w http.ResponseWriter, r *http.Request, p auth.Principal) {
	view, err := s.Tenancy.Get(r.Context(), p.UserID, r.PathValue("id"))
	if err != nil {
		s.tenancyError(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, tenantJSON(view))
}

type renameTenantRequest struct {
	Name string `json:"name"`
}

func (s *Server) renameTenant(w http.ResponseWriter, r *http.Request, p auth.Principal) {
	var body renameTenantRequest
	if !decode(w, r, &body) {
		return
	}
	view, err := s.Tenancy.Rename(r.Context(), p.UserID, r.PathValue("id"), body.Name)
	if err != nil {
		s.tenancyError(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, tenantJSON(view))
}

func (s *Server) deleteTenant(w http.ResponseWriter, r *http.Request, p auth.Principal) {
	if err := s.Tenancy.Delete(r.Context(), p.UserID, r.PathValue("id")); err != nil {
		s.tenancyError(w, r, err)
		return
	}
	w.WriteHeader(http.StatusNoContent)
}

func memberJSON(m tenancy.MemberView) map[string]any {
	return map[string]any{"user_id": m.UserID, "email": m.Email, "role": m.Role, "joined_at": m.JoinedAt.UTC().Format(time.RFC3339)}
}

func (s *Server) listMembers(w http.ResponseWriter, r *http.Request, p auth.Principal) {
	members, err := s.Tenancy.Members(r.Context(), p.UserID, r.PathValue("id"))
	if err != nil {
		s.tenancyError(w, r, err)
		return
	}
	items := make([]map[string]any, 0, len(members))
	for _, m := range members {
		items = append(items, memberJSON(m))
	}
	writeJSON(w, http.StatusOK, map[string]any{"members": items})
}

type changeRoleRequest struct {
	Role string `json:"role"`
}

func (s *Server) changeRole(w http.ResponseWriter, r *http.Request, p auth.Principal) {
	var body changeRoleRequest
	if !decode(w, r, &body) {
		return
	}
	member, err := s.Tenancy.ChangeRole(r.Context(), p.UserID, r.PathValue("id"), r.PathValue("user_id"), body.Role)
	if err != nil {
		s.tenancyError(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, memberJSON(member))
}

func (s *Server) removeMember(w http.ResponseWriter, r *http.Request, p auth.Principal) {
	if err := s.Tenancy.RemoveMember(r.Context(), p.UserID, r.PathValue("id"), r.PathValue("user_id")); err != nil {
		s.tenancyError(w, r, err)
		return
	}
	w.WriteHeader(http.StatusNoContent)
}

func inviteJSON(i tenancy.Invite) map[string]any {
	return map[string]any{"id": i.ID, "email": i.Email, "role": i.Role,
		"expires_at": i.ExpiresAt.UTC().Format(time.RFC3339), "created_at": i.CreatedAt.UTC().Format(time.RFC3339)}
}

func (s *Server) listInvites(w http.ResponseWriter, r *http.Request, p auth.Principal) {
	invites, err := s.Tenancy.PendingInvites(r.Context(), p.UserID, r.PathValue("id"))
	if err != nil {
		s.tenancyError(w, r, err)
		return
	}
	items := make([]map[string]any, 0, len(invites))
	for _, i := range invites {
		items = append(items, inviteJSON(i))
	}
	writeJSON(w, http.StatusOK, map[string]any{"invites": items})
}

type createInviteRequest struct {
	Email string `json:"email"`
	Role  string `json:"role"`
}

func (s *Server) createInvite(w http.ResponseWriter, r *http.Request, p auth.Principal) {
	var body createInviteRequest
	if !decode(w, r, &body) {
		return
	}
	invite, err := s.Tenancy.Invite(r.Context(), p.UserID, r.PathValue("id"), body.Email, body.Role)
	if err != nil {
		s.tenancyError(w, r, err)
		return
	}
	// The token is only in the email, never in the response.
	writeJSON(w, http.StatusCreated, inviteJSON(invite))
}

func (s *Server) revokeInvite(w http.ResponseWriter, r *http.Request, p auth.Principal) {
	if err := s.Tenancy.RevokeInvite(r.Context(), p.UserID, r.PathValue("id"), r.PathValue("invite_id")); err != nil {
		s.tenancyError(w, r, err)
		return
	}
	w.WriteHeader(http.StatusNoContent)
}

func (s *Server) acceptInvite(w http.ResponseWriter, r *http.Request, p auth.Principal) {
	view, err := s.Tenancy.AcceptInvite(r.Context(), p.UserID, r.PathValue("token"))
	if err != nil {
		s.tenancyError(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, tenantJSON(view))
}

func (s *Server) listDevices(w http.ResponseWriter, r *http.Request, p auth.Principal) {
	devices, err := s.Auth.Devices(r.Context(), p)
	if err != nil {
		s.internal(w, r, err)
		return
	}
	items := make([]map[string]any, 0, len(devices))
	for _, d := range devices {
		items = append(items, map[string]any{"id": d.ID, "name": d.Name, "platform": d.Platform,
			"last_seen_at": d.SeenAt.UTC().Format(time.RFC3339), "current": d.ID == p.DeviceID})
	}
	writeJSON(w, http.StatusOK, map[string]any{"devices": items})
}

func (s *Server) revokeDevice(w http.ResponseWriter, r *http.Request, p auth.Principal) {
	err := s.Auth.RevokeDevice(r.Context(), p, r.PathValue("id"))
	if errors.Is(err, auth.ErrNotFound) {
		writeError(w, r, http.StatusNotFound, "NOT_FOUND", "No such device.")
		return
	}
	if err != nil {
		s.internal(w, r, err)
		return
	}
	w.WriteHeader(http.StatusNoContent)
}

func (s *Server) deleteAccount(w http.ResponseWriter, r *http.Request, p auth.Principal) {
	if err := s.Tenancy.DeleteAccount(r.Context(), p.UserID); err != nil {
		s.tenancyError(w, r, err)
		return
	}
	w.WriteHeader(http.StatusNoContent)
}

func (s *Server) tenancyError(w http.ResponseWriter, r *http.Request, err error) {
	var transfer *tenancy.OwnershipTransferRequiredError
	switch {
	case errors.As(err, &transfer):
		writeJSON(w, http.StatusConflict, map[string]any{"error": map[string]any{
			"code": "OWNERSHIP_TRANSFER_REQUIRED", "message": "Make someone else an owner of these spaces, or delete them, first.",
			"request_id": requestID(r), "tenant_ids": transfer.TenantIDs,
		}})
	case errors.Is(err, tenancy.ErrNotFound):
		writeError(w, r, http.StatusNotFound, "NOT_FOUND", "No such space, member, or invitation.")
	case errors.Is(err, tenancy.ErrForbidden):
		writeError(w, r, http.StatusForbidden, "FORBIDDEN", "Your role in this space does not allow this.")
	case errors.Is(err, tenancy.ErrLastOwner):
		writeError(w, r, http.StatusConflict, "LAST_OWNER", "A space needs at least one owner. Make someone else an owner first.")
	case errors.Is(err, tenancy.ErrPersonalSpace):
		writeError(w, r, http.StatusConflict, "PERSONAL_SPACE", "A Personal space has only its owner.")
	case errors.Is(err, tenancy.ErrAlreadyMember):
		writeError(w, r, http.StatusConflict, "ALREADY_MEMBER", "This person is already a member.")
	case errors.Is(err, tenancy.ErrInviteInvalid):
		writeError(w, r, http.StatusNotFound, "INVITE_INVALID", "This invitation is invalid, expired, used, or revoked.")
	case errors.Is(err, tenancy.ErrInviteEmailMismatch):
		writeError(w, r, http.StatusForbidden, "INVITE_EMAIL_MISMATCH", "This invitation is for a different email address. Sign in with that address.")
	case errors.Is(err, tenancy.ErrInvalidName):
		writeError(w, r, http.StatusUnprocessableEntity, "INVALID_NAME", "Enter a name of 1 to 100 characters.")
	case errors.Is(err, tenancy.ErrInvalidType):
		writeError(w, r, http.StatusUnprocessableEntity, "INVALID_TYPE", "Choose personal, family, or organization.")
	case errors.Is(err, tenancy.ErrInvalidRole):
		writeError(w, r, http.StatusUnprocessableEntity, "INVALID_ROLE", "Choose a valid role.")
	case errors.Is(err, auth.ErrInvalidEmail):
		writeError(w, r, http.StatusUnprocessableEntity, "INVALID_EMAIL", "Enter a valid email address.")
	case errors.Is(err, tenancy.ErrTooManyInvites):
		w.Header().Set("Retry-After", "3600")
		writeError(w, r, http.StatusTooManyRequests, "RATE_LIMITED", "Too many invitations. Try again later.")
	case errors.Is(err, tenancy.ErrDeliveryFailed):
		writeError(w, r, http.StatusServiceUnavailable, "EMAIL_DELIVERY_FAILED", "The invitation email could not be sent. Try again in a few minutes.")
	default:
		s.internal(w, r, err)
	}
}
