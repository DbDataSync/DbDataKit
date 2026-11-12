export type EditErrorCode =
  | 'unknown_widget' | 'unknown_layout' | 'unknown_parameter' | 'name_taken' | 'bad_name' | 'last_layout' | 'not_placeable' | 'already_placed' | 'unknown_well' | 'wrong_member_kind' | 'bad_field' | 'bad_position' | 'bad_option'
  // the report editor (DBR27)
  | 'not_flow' | 'bad_index' | 'last_section' | 'bad_value'
  // the link editor (DBR38)
  | 'unknown_link' | 'not_a_form' | 'not_clickable'
  // the action links (DBR53)
  | 'not_an_action' | 'nothing_to_set'

/** What a command refuses with: a code a form can act on and a sentence a person can read. */
export class EditError extends Error {
  readonly code: EditErrorCode
  constructor(code: EditErrorCode, message: string) {
    super(message)
    this.name = 'EditError'
    this.code = code
  }
}
