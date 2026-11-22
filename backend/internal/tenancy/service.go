package tenancy

import (
	"context"
	"crypto/hmac"
	"crypto/rand"
	"crypto/sha256"
	"encoding/base64"
	"errors"
	"log/slog"
	"sort"
	"strings"
	"time"

	"alondra1/backend/internal/auth"
)

// InviteTokenPrefix makes leaked invitation tokens recognizable by secret scanners.
const InviteTokenPrefix = "a1i_"

type Tenant struct {
	ID        string
	Type      Type
	Name      string
	CreatedBy string
	CreatedAt time.Time
	UpdatedAt time.Time
}

type Membership struct {
	TenantID  string
	UserID    string
	Role      Role
	Active    bool
	CreatedAt time.Time
}

// View is a space as one member sees it.
type View struct {
	Tenant
	Role Role
}

type MemberView struct {
	UserID   string
	Email    string
	Role     Role
	JoinedAt time.Time
}

type Invite struct {
	ID         string
	TenantID   string
	Email      string
	Role       Role
	TokenHash  []byte
	ExpiresAt  time.Time
	AcceptedAt *time.Time
	RevokedAt  *time.Time
	InvitedBy  string
	CreatedAt  time.Time
}

func (i Invite) pending(now time.Time) bool {
	return i.AcceptedAt == nil && i.RevokedAt == nil && now.Before(i.ExpiresAt)
}

// Tx is one database transaction. Mutating flows call LockTenant first, which serializes all changes to a space.
type Tx interface {
	LockTenant(ctx context.Context, id string) (Tenant, error) // ErrNotFound if missing or deleted
	InsertTenant(ctx context.Context, t Tenant) error
	RenameTenant(ctx context.Context, id, name string, now time.Time) error
	SoftDeleteTenant(ctx context.Context, id string, now time.Time) error

	Membership(ctx context.Context, tenantID, userID string) (Membership, error) // ErrNotFound if none
	CountActive(ctx context.Context, tenantID string) (owners, members int, err error)
	UpsertMembership(ctx context.Context, m Membership, now time.Time) error
	RemoveAllMemberships(ctx context.Context, tenantID string, now time.Time) error
	ListMembers(ctx context.Context, tenantID string) ([]MemberView, error)
	ListMemberships(ctx context.Context, userID string) ([]View, error) // active memberships of live spaces

	InsertInvite(ctx context.Context, i Invite) error
	InviteByTokenHash(ctx context.Context, hash []byte) (Invite, error) // locks the row
	Invite(ctx context.Context, tenantID, id string) (Invite, error)
	ListPendingInvites(ctx context.Context, tenantID string, now time.Time) ([]Invite, error)
	InvitesCreatedSince(ctx context.Context, tenantID string, since time.Time) (int, error)
	RevokePendingInvites(ctx context.Context, tenantID, email string, now time.Time) error // email "" = all in tenant
	RevokePendingInvitesTo(ctx context.Context, email string, now time.Time) error         // in every space
	RevokeInvite(ctx context.Context, id string, now time.Time) error
	MarkInviteAccepted(ctx context.Context, id, userID string, now time.Time) error

	UserEmail(ctx context.Context, userID string) (string, error)
	DeleteUser(ctx context.Context, userID string, now time.Time) error // revoke sessions/devices, pseudonymize email
	Audit(ctx context.Context, e auth.AuditEvent) error
}

type Store interface {
	InTx(ctx context.Context, fn func(Tx) error) error
}

// InviteMailer sends invitation emails. The token appears only in the email.
type InviteMailer interface {
	SendInvite(ctx context.Context, to, spaceName, inviterEmail, token string, validFor time.Duration) error
}

type Service struct {
	store  Store
	mailer InviteMailer
	now    func() time.Time
	log    *slog.Logger
}

func NewService(store Store, mailer InviteMailer, now func() time.Time, log *slog.Logger) *Service {
	if log == nil {
		log = slog.Default()
	}
	return &Service{store: store, mailer: mailer, now: now, log: log}
}

// Create makes a space of the chosen type with the caller as its owner. The type is never inferred.
func (s *Service) Create(ctx context.Context, userID, rawType, rawName string) (View, error) {
	t, err := ParseType(rawType)
	if err != nil {
		return View{}, err
	}
	name, err := CleanName(rawName, t)
	if err != nil {
		return View{}, err
	}
	now := s.now().UTC()
	id, err := auth.NewUUIDv7(now)
	if err != nil {
		return View{}, err
	}
	tenant := Tenant{ID: id, Type: t, Name: name, CreatedBy: userID, CreatedAt: now, UpdatedAt: now}
	err = s.store.InTx(ctx, func(tx Tx) error {
		if err := tx.InsertTenant(ctx, tenant); err != nil {
			return err
		}
		if err := tx.UpsertMembership(ctx, Membership{TenantID: id, UserID: userID, Role: Owner, Active: true, CreatedAt: now}, now); err != nil {
			return err
		}
		return tx.Audit(ctx, auth.AuditEvent{TenantID: id, ActorUserID: userID, EventType: "tenant.created", TargetID: id, Metadata: map[string]string{"type": string(t)}, CreatedAt: now})
	})
	if err != nil {
		return View{}, err
	}
	return View{Tenant: tenant, Role: Owner}, nil
}

func (s *Service) List(ctx context.Context, userID string) ([]View, error) {
	var views []View
	err := s.store.InTx(ctx, func(tx Tx) (err error) {
		views, err = tx.ListMemberships(ctx, userID)
		return err
	})
	return views, err
}

// member loads the space and the caller's active role; anything else is ErrNotFound.
func member(ctx context.Context, tx Tx, tenantID, userID string) (Tenant, Role, error) {
	if !auth.IsUUID(tenantID) {
		return Tenant{}, "", ErrNotFound
	}
	tenant, err := tx.LockTenant(ctx, tenantID)
	if err != nil {
		return Tenant{}, "", err
	}
	m, err := tx.Membership(ctx, tenantID, userID)
	if errors.Is(err, ErrNotFound) || (err == nil && !m.Active) {
		return Tenant{}, "", ErrNotFound
	}
	if err != nil {
		return Tenant{}, "", err
	}
	return tenant, m.Role, nil
}

func (s *Service) Get(ctx context.Context, userID, tenantID string) (View, error) {
	var view View
	err := s.store.InTx(ctx, func(tx Tx) error {
		tenant, role, err := member(ctx, tx, tenantID, userID)
		view = View{Tenant: tenant, Role: role}
		return err
	})
	return view, err
}

func (s *Service) Rename(ctx context.Context, userID, tenantID, rawName string) (View, error) {
	var view View
	err := s.store.InTx(ctx, func(tx Tx) error {
		tenant, role, err := member(ctx, tx, tenantID, userID)
		if err != nil {
			return err
		}
		if !CanManage(role) {
			return ErrForbidden
		}
		name, err := CleanName(rawName, tenant.Type)
		if err != nil {
			return err
		}
		now := s.now().UTC()
		if err := tx.RenameTenant(ctx, tenantID, name, now); err != nil {
			return err
		}
		tenant.Name, tenant.UpdatedAt = name, now
		view = View{Tenant: tenant, Role: role}
		return tx.Audit(ctx, auth.AuditEvent{TenantID: tenantID, ActorUserID: userID, EventType: "tenant.renamed", TargetID: tenantID, CreatedAt: now})
	})
	return view, err
}

// Delete soft-deletes a space: memberships end, pending invitations are revoked. Data purge and sync tombstones follow
// the retention policy in docs/decisions/0003-retention.md.
func (s *Service) Delete(ctx context.Context, userID, tenantID string) error {
	return s.store.InTx(ctx, func(tx Tx) error {
		_, role, err := member(ctx, tx, tenantID, userID)
		if err != nil {
			return err
		}
		if !CanDelete(role) {
			return ErrForbidden
		}
		return deleteTenant(ctx, tx, tenantID, userID, s.now().UTC())
	})
}

func deleteTenant(ctx context.Context, tx Tx, tenantID, actorID string, now time.Time) error {
	if err := tx.RevokePendingInvites(ctx, tenantID, "", now); err != nil {
		return err
	}
	if err := tx.RemoveAllMemberships(ctx, tenantID, now); err != nil {
		return err
	}
	if err := tx.SoftDeleteTenant(ctx, tenantID, now); err != nil {
		return err
	}
	return tx.Audit(ctx, auth.AuditEvent{TenantID: tenantID, ActorUserID: actorID, EventType: "tenant.deleted", TargetID: tenantID, CreatedAt: now})
}

func (s *Service) Members(ctx context.Context, userID, tenantID string) ([]MemberView, error) {
	var members []MemberView
	err := s.store.InTx(ctx, func(tx Tx) error {
		if _, _, err := member(ctx, tx, tenantID, userID); err != nil {
			return err
		}
		var err error
		members, err = tx.ListMembers(ctx, tenantID)
		return err
	})
	return members, err
}

func (s *Service) PendingInvites(ctx context.Context, userID, tenantID string) ([]Invite, error) {
	var invites []Invite
	err := s.store.InTx(ctx, func(tx Tx) error {
		_, role, err := member(ctx, tx, tenantID, userID)
		if err != nil {
			return err
		}
		if !CanManage(role) {
			return ErrForbidden
		}
		invites, err = tx.ListPendingInvites(ctx, tenantID, s.now().UTC())
		return err
	})
	return invites, err
}

// Invite emails a one-time invitation to the address. A new invitation replaces a pending one for the same address.
func (s *Service) Invite(ctx context.Context, userID, tenantID, rawEmail, rawRole string) (Invite, error) {
	email, err := auth.NormalizeEmail(rawEmail)
	if err != nil {
		return Invite{}, err
	}
	invited, err := ParseRole(rawRole)
	if err != nil {
		return Invite{}, err
	}
	now := s.now().UTC()
	token, hash, err := newInviteToken()
	if err != nil {
		return Invite{}, err
	}
	id, err := auth.NewUUIDv4()
	if err != nil {
		return Invite{}, err
	}
	invite := Invite{ID: id, TenantID: tenantID, Email: email, Role: invited, TokenHash: hash, ExpiresAt: now.Add(InviteTTL), InvitedBy: userID, CreatedAt: now}
	var spaceName, inviterEmail string
	err = s.store.InTx(ctx, func(tx Tx) error {
		tenant, role, err := member(ctx, tx, tenantID, userID)
		if err != nil {
			return err
		}
		if err := CanInvite(role, invited, tenant.Type); err != nil {
			return err
		}
		recent, err := tx.InvitesCreatedSince(ctx, tenantID, now.Add(-time.Hour))
		if err != nil {
			return err
		}
		pending, err := tx.ListPendingInvites(ctx, tenantID, now)
		if err != nil {
			return err
		}
		if recent >= MaxInvitesPerHour || len(pending) >= MaxPendingInvites {
			return ErrTooManyInvites
		}
		members, err := tx.ListMembers(ctx, tenantID)
		if err != nil {
			return err
		}
		for _, m := range members {
			if m.Email == email {
				return ErrAlreadyMember
			}
		}
		if err := tx.RevokePendingInvites(ctx, tenantID, email, now); err != nil {
			return err
		}
		if err := tx.InsertInvite(ctx, invite); err != nil {
			return err
		}
		spaceName = tenant.Name
		if inviterEmail, err = tx.UserEmail(ctx, userID); err != nil {
			return err
		}
		return tx.Audit(ctx, auth.AuditEvent{TenantID: tenantID, ActorUserID: userID, EventType: "invite.created", TargetID: id,
			Metadata: map[string]string{"role": string(invited), "to": auth.RedactEmail(email)}, CreatedAt: now})
	})
	if err != nil {
		return Invite{}, err
	}
	if err := s.mailer.SendInvite(ctx, email, spaceName, inviterEmail, token, InviteTTL); err != nil {
		s.log.Warn("invitation email not sent; invitation revoked", "invite_id", id, "error", err.Error())
		_ = s.store.InTx(context.WithoutCancel(ctx), func(tx Tx) error { return tx.RevokeInvite(ctx, id, s.now().UTC()) })
		return Invite{}, ErrDeliveryFailed
	}
	return invite, nil
}

func (s *Service) RevokeInvite(ctx context.Context, userID, tenantID, inviteID string) error {
	return s.store.InTx(ctx, func(tx Tx) error {
		_, role, err := member(ctx, tx, tenantID, userID)
		if err != nil {
			return err
		}
		if !CanManage(role) {
			return ErrForbidden
		}
		if !auth.IsUUID(inviteID) {
			return ErrNotFound
		}
		invite, err := tx.Invite(ctx, tenantID, inviteID)
		if err != nil {
			return err
		}
		now := s.now().UTC()
		if !invite.pending(now) {
			return ErrNotFound
		}
		if err := tx.RevokeInvite(ctx, inviteID, now); err != nil {
			return err
		}
		return tx.Audit(ctx, auth.AuditEvent{TenantID: tenantID, ActorUserID: userID, EventType: "invite.revoked", TargetID: inviteID, CreatedAt: now})
	})
}

// AcceptInvite joins the space. Only the signed-in account whose email matches the invitation can accept it.
func (s *Service) AcceptInvite(ctx context.Context, userID, token string) (View, error) {
	if !strings.HasPrefix(token, InviteTokenPrefix) || len(token) > 128 {
		return View{}, ErrInviteInvalid
	}
	hash := sha256.Sum256([]byte(token))
	var view View
	err := s.store.InTx(ctx, func(tx Tx) error {
		invite, err := tx.InviteByTokenHash(ctx, hash[:])
		if errors.Is(err, ErrNotFound) {
			return ErrInviteInvalid
		}
		if err != nil {
			return err
		}
		tenant, err := tx.LockTenant(ctx, invite.TenantID)
		if errors.Is(err, ErrNotFound) {
			return ErrInviteInvalid
		}
		if err != nil {
			return err
		}
		now := s.now().UTC()
		if !invite.pending(now) {
			return ErrInviteInvalid
		}
		email, err := tx.UserEmail(ctx, userID)
		if err != nil {
			return err
		}
		if !hmac.Equal([]byte(email), []byte(invite.Email)) {
			return ErrInviteEmailMismatch
		}
		existing, err := tx.Membership(ctx, tenant.ID, userID)
		if err == nil && existing.Active {
			return ErrAlreadyMember
		}
		if err != nil && !errors.Is(err, ErrNotFound) {
			return err
		}
		if err := tx.UpsertMembership(ctx, Membership{TenantID: tenant.ID, UserID: userID, Role: invite.Role, Active: true, CreatedAt: now}, now); err != nil {
			return err
		}
		if err := tx.MarkInviteAccepted(ctx, invite.ID, userID, now); err != nil {
			return err
		}
		view = View{Tenant: tenant, Role: invite.Role}
		return tx.Audit(ctx, auth.AuditEvent{TenantID: tenant.ID, ActorUserID: userID, EventType: "invite.accepted", TargetID: invite.ID, CreatedAt: now})
	})
	return view, err
}

// RemoveMember removes someone, or lets the caller leave. A space never loses its last owner while others remain.
func (s *Service) RemoveMember(ctx context.Context, userID, tenantID, targetID string) error {
	return s.store.InTx(ctx, func(tx Tx) error {
		tenant, role, err := member(ctx, tx, tenantID, userID)
		if err != nil {
			return err
		}
		self := targetID == userID
		target, err := tx.Membership(ctx, tenantID, targetID)
		if errors.Is(err, ErrNotFound) || (err == nil && !target.Active) {
			return ErrNotFound
		}
		if err != nil {
			return err
		}
		if err := CanRemove(role, target.Role, self); err != nil {
			return err
		}
		if tenant.Type == Personal {
			return ErrPersonalSpace // the owner deletes a Personal space instead of leaving it
		}
		owners, _, err := tx.CountActive(ctx, tenantID)
		if err != nil {
			return err
		}
		if target.Role == Owner && owners <= 1 {
			return ErrLastOwner
		}
		now := s.now().UTC()
		target.Active = false
		if err := tx.UpsertMembership(ctx, target, now); err != nil {
			return err
		}
		event := "member.removed"
		if self {
			event = "member.left"
		}
		return tx.Audit(ctx, auth.AuditEvent{TenantID: tenantID, ActorUserID: userID, EventType: event, TargetID: targetID, CreatedAt: now})
	})
}

// ChangeRole lets an owner set any member's role; making someone else owner is how ownership is transferred.
func (s *Service) ChangeRole(ctx context.Context, userID, tenantID, targetID, rawRole string) (MemberView, error) {
	newRole, err := ParseRole(rawRole)
	if err != nil {
		return MemberView{}, err
	}
	var result MemberView
	err = s.store.InTx(ctx, func(tx Tx) error {
		tenant, role, err := member(ctx, tx, tenantID, userID)
		if err != nil {
			return err
		}
		if err := CanChangeRole(role); err != nil {
			return err
		}
		if tenant.Type == Personal {
			return ErrPersonalSpace
		}
		target, err := tx.Membership(ctx, tenantID, targetID)
		if errors.Is(err, ErrNotFound) || (err == nil && !target.Active) {
			return ErrNotFound
		}
		if err != nil {
			return err
		}
		owners, _, err := tx.CountActive(ctx, tenantID)
		if err != nil {
			return err
		}
		if target.Role == Owner && newRole != Owner && owners <= 1 {
			return ErrLastOwner
		}
		now := s.now().UTC()
		old := target.Role
		target.Role = newRole
		if err := tx.UpsertMembership(ctx, target, now); err != nil {
			return err
		}
		email, err := tx.UserEmail(ctx, targetID)
		if err != nil {
			return err
		}
		result = MemberView{UserID: targetID, Email: email, Role: newRole, JoinedAt: target.CreatedAt}
		return tx.Audit(ctx, auth.AuditEvent{TenantID: tenantID, ActorUserID: userID, EventType: "member.role_changed", TargetID: targetID,
			Metadata: map[string]string{"from": string(old), "to": string(newRole)}, CreatedAt: now})
	})
	return result, err
}

// DeleteAccount deletes the caller's account: spaces where the caller is the only member are deleted, other
// memberships end, every session and device is revoked, and the email address is released (pseudonymized). It is
// refused while the caller is the only owner of a space that still has other members.
func (s *Service) DeleteAccount(ctx context.Context, userID string) error {
	return s.store.InTx(ctx, func(tx Tx) error {
		now := s.now().UTC()
		memberships, err := tx.ListMemberships(ctx, userID)
		if err != nil {
			return err
		}
		// Lock spaces in id order so concurrent deletions cannot deadlock.
		sort.Slice(memberships, func(i, j int) bool { return memberships[i].ID < memberships[j].ID })
		var blocked []string
		for _, m := range memberships {
			if _, err := tx.LockTenant(ctx, m.ID); err != nil {
				return err
			}
			owners, members, err := tx.CountActive(ctx, m.ID)
			if err != nil {
				return err
			}
			switch {
			case members <= 1:
				if err := deleteTenant(ctx, tx, m.ID, userID, now); err != nil {
					return err
				}
			case m.Role == Owner && owners <= 1:
				blocked = append(blocked, m.ID)
			default:
				if err := tx.UpsertMembership(ctx, Membership{TenantID: m.ID, UserID: userID, Role: m.Role, Active: false}, now); err != nil {
					return err
				}
			}
		}
		if len(blocked) > 0 {
			return &OwnershipTransferRequiredError{TenantIDs: blocked}
		}
		email, err := tx.UserEmail(ctx, userID)
		if err != nil {
			return err
		}
		if err := tx.RevokePendingInvitesTo(ctx, email, now); err != nil {
			return err
		}
		if err := tx.Audit(ctx, auth.AuditEvent{ActorUserID: userID, EventType: "account.deleted", TargetID: userID, CreatedAt: now}); err != nil {
			return err
		}
		return tx.DeleteUser(ctx, userID, now)
	})
}

func newInviteToken() (string, []byte, error) {
	raw := make([]byte, 32)
	if _, err := rand.Read(raw); err != nil {
		return "", nil, err
	}
	token := InviteTokenPrefix + base64.RawURLEncoding.EncodeToString(raw)
	hash := sha256.Sum256([]byte(token))
	return token, hash[:], nil
}
