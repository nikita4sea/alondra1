// Package tenancy implements spaces (tenants): Personal, Family, and Organization, their memberships and roles,
// invitations, and account deletion (docs/BACKEND_SPEC.md §5). The rules live here; the store only runs SQL.
package tenancy

import (
	"errors"
	"fmt"
	"strings"
	"time"
	"unicode"
	"unicode/utf8"
)

type Type string

const (
	Personal     Type = "personal"
	Family       Type = "family"
	Organization Type = "organization"
)

type Role string

const (
	Owner  Role = "owner"
	Admin  Role = "admin"
	Member Role = "member"
)

func (r Role) rank() int {
	switch r {
	case Owner:
		return 3
	case Admin:
		return 2
	case Member:
		return 1
	}
	return 0
}

// Errors mapped to HTTP responses. ErrNotFound is also used when the caller is not a member, so ids of other spaces
// cannot be probed.
var (
	ErrNotFound            = errors.New("space not found")
	ErrForbidden           = errors.New("your role does not allow this")
	ErrLastOwner           = errors.New("a space needs at least one owner")
	ErrPersonalSpace       = errors.New("a Personal space has only its owner")
	ErrAlreadyMember       = errors.New("already a member")
	ErrInviteInvalid       = errors.New("the invitation is invalid, expired, used, or revoked")
	ErrInviteEmailMismatch = errors.New("the invitation is for a different email address")
	ErrInvalidName         = errors.New("name must be 1 to 100 characters")
	ErrInvalidType         = errors.New("type must be personal, family, or organization")
	ErrInvalidRole         = errors.New("invalid role")
	ErrTooManyInvites      = errors.New("too many invitations; try again later")
	ErrDeliveryFailed      = errors.New("the invitation email could not be sent")
)

// OwnershipTransferRequiredError lists the spaces where the caller is the only owner while others are still members.
type OwnershipTransferRequiredError struct{ TenantIDs []string }

func (e *OwnershipTransferRequiredError) Error() string {
	return fmt.Sprintf("transfer ownership of %d space(s) before deleting the account", len(e.TenantIDs))
}

const (
	InviteTTL             = 7 * 24 * time.Hour
	MaxInvitesPerHour     = 20
	MaxPendingInvites     = 100
	DefaultPersonalName   = "My space"
	maxNameRunes          = 100
)

func ParseType(value string) (Type, error) {
	switch Type(strings.ToLower(strings.TrimSpace(value))) {
	case Personal:
		return Personal, nil
	case Family:
		return Family, nil
	case Organization:
		return Organization, nil
	}
	return "", ErrInvalidType
}

func ParseRole(value string) (Role, error) {
	switch Role(strings.ToLower(strings.TrimSpace(value))) {
	case Owner:
		return Owner, nil
	case Admin:
		return Admin, nil
	case Member:
		return Member, nil
	}
	return "", ErrInvalidRole
}

// CleanName trims a display name, removes control characters, and checks its length. A Personal space may omit it.
func CleanName(name string, t Type) (string, error) {
	name = strings.TrimSpace(strings.Map(func(r rune) rune {
		if unicode.IsControl(r) {
			return -1
		}
		return r
	}, name))
	if name == "" && t == Personal {
		return DefaultPersonalName, nil
	}
	if name == "" || utf8.RuneCountInString(name) > maxNameRunes {
		return "", ErrInvalidName
	}
	return name, nil
}

// CanManage: rename the space and manage invitations.
func CanManage(r Role) bool { return r == Owner || r == Admin }

// CanDelete: only owners delete a space.
func CanDelete(r Role) bool { return r == Owner }

// CanInvite: owners and admins invite into Family and Organization spaces, never above their own role, never as owner
// (ownership changes only through a role change by an owner).
func CanInvite(actor, invited Role, t Type) error {
	if t == Personal {
		return ErrPersonalSpace
	}
	if !CanManage(actor) {
		return ErrForbidden
	}
	if invited != Admin && invited != Member {
		return ErrInvalidRole
	}
	if invited.rank() > actor.rank() {
		return ErrForbidden
	}
	return nil
}

// CanRemove: anyone may leave; owners remove anyone; admins remove members only. The last-owner rule is checked
// separately because it depends on the other memberships.
func CanRemove(actor, target Role, self bool) error {
	switch {
	case self:
		return nil
	case actor == Owner:
		return nil
	case actor == Admin && target == Member:
		return nil
	}
	return ErrForbidden
}

// CanChangeRole: only owners change roles, including making someone else an owner (ownership transfer).
func CanChangeRole(actor Role) error {
	if actor != Owner {
		return ErrForbidden
	}
	return nil
}
