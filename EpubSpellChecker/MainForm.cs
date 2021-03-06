﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace EpubSpellChecker
{
    /// <summary>
    /// The main form where the user can open an epub, see the word list, correct words and save the changes
    /// </summary>
    public partial class MainForm : Form
    {

        private SpellCheckManager manager;
        private Epub currentEpub;

        private Loader loader;

        public MainForm()
        {
            InitializeComponent();
            // reduce flickering by setting the datagridview and listbox to double buffered drawing
            grid.DoubleBuffered(true);
            lstOccurrences.DoubleBuffered(true);

            // create a new async loader to update the status and progress bar
            loader = new Loader(lblStatus, progress);

            // initialize a spell check manager that will do most of the word logic
            List<string> warnings;
            manager = new SpellCheckManager(out warnings);

            // if there are warnings (like missing dictionary.txt) show them
            if (warnings.Count > 0)
                MessageBox.Show(this, "Warning: " + Environment.NewLine + string.Join(Environment.NewLine, warnings.Select(w => " - " + w)), "Warnings while loading data", MessageBoxButtons.OK);
        }

        /// <summary>
        /// Loads an epub file and analyse all the words
        /// </summary>
        /// <param name="path">The full path to the epub file</param>
        public void OpenEpub(string path)
        {
            // cancel any previous loading action and reset the grid and listbox
            grid.DataSource = null;
            lstOccurrences.Items.Clear();
            loader.CancelAll();

            // read the epub file structure 
            Epub epub = Epub.FromFile(path);

            // change the caption of the form with the filename
            Text = "Epub spell checker - " + System.IO.Path.GetFileName(path);

            // analyse the epub async
            loader.LoadAsync<Dictionary<string, WordEntry>>((state) =>
            {
                // set progress to marquee
                state.Text = "Loading epub...";
                state.Progress = -1;

                // get all the word entries in the book
                var wordEntries = manager.AnalyseEpub(epub);
                return wordEntries;
            }, wes =>
            {
                // if there was a previously loaded epub, dispose it
                if (currentEpub != null)
                    currentEpub.Dispose();

                currentEpub = epub;

                // bind the word entry list to the datagridview
                var bs = new SortableBindingList<WordEntry>(wes.Values);
                grid.DataSource = bs;

                // update the grid to match the current filter
                ApplyFilter(false);

                // update statistics of the word entry list
                UpdateStatistics();

                // continue with loading suggestions for each unknown word
                FillSuggestions(wes);

                CheckEditMenuItemAvailibility();
            });
        }

        /// <summary>
        /// Creates suggestions for all the word entries (async)
        /// </summary>
        /// <param name="wordEntries"></param>
        private void FillSuggestions(Dictionary<string, WordEntry> wordEntries)
        {
            loader.LoadAsync<Dictionary<string, Dictionary<string, int>>>((state) =>
            {

                state.Text = "Building suggestions...";
                int count = 0;

                var enabledTests = new HashSet<string>(SettingsManager.GetSettings().EnabledTests);

                Dictionary<string, Dictionary<string, int>> ocrPatternsAppliedCount = new Dictionary<string, Dictionary<string, int>>();

                // process all word entries in parallel
                Parallel.ForEach(wordEntries.Values, (we, loop) =>
                {
                    // if the action was cancelled, break the loop to stop processing
                    if (state.Cancel)
                        loop.Break();


                    // build a suggestion for the current word entry
                    manager.FillSuggestion(we, wordEntries, ocrPatternsAppliedCount, enabledTests);

                    // update the progress
                    state.Progress = count++ / (float)wordEntries.Count;
                    state.Text = "Building suggestions (" + count + " / " + wordEntries.Count + ")";
                });

                // if the action was cancelled, don't return partial data
                if (state.Cancel)
                    return null;

                // return the ocr patterns that have been applied to build the suggestions. This is needed to detect warnings
                return ocrPatternsAppliedCount;
            }, ocrpatternDicCount =>
            {
                // if the action wasn't cancelled
                if (ocrpatternDicCount != null)
                {
                    // update the filter, the states of some word entries can be changed
                    ApplyFilter(true);
                    // update the statistics accordingly
                    UpdateStatistics();

                    // check the word entries for warnings based on the applied OCR patterns
                    FillWarnings(wordEntries, ocrpatternDicCount);
                }
            });
        }

        /// <summary>
        /// Check all word entries for warnings (words that can be ambigious with the applied ocr patterns
        /// e.g die -> the when the ocr pattern "d -> th" was succesful in other words)
        /// </summary>
        /// <param name="wordEntries">The list of word entries</param>
        /// <param name="ocrPatternsAppliedCount">The OCR patterns that helped build suggestions for words that aren't in the dictionary</param>
        private void FillWarnings(Dictionary<string, WordEntry> wordEntries, Dictionary<string, Dictionary<string, int>> ocrPatternsAppliedCount)
        {
            loader.LoadAsync<bool>((state) =>
            {
                state.Text = "Analyzing possible OCR errors on valid words...";
                int count = 0;

                Parallel.ForEach(wordEntries.Values, (we, loop) =>
                {
                    // if the action was cancelled, break the loop to stop processing
                    if (state.Cancel)
                        loop.Break();


                    // check for warning on the current word entry
                    manager.FillWarnings(we, wordEntries, ocrPatternsAppliedCount);

                    // update the progress
                    state.Progress = count++ / (float)wordEntries.Count;
                    state.Text = "Building warnings (" + count + " / " + wordEntries.Count + ")";
                });

                // if the action was cancelled, don't continue
                if (state.Cancel)
                    return false;

                return true;
            }, isComplete =>
            {
                // if the action was completed
                if (isComplete)
                {
                    // update the filter and statistics
                    UpdateStatistics();
                    ApplyFilter(true);
                }
            });
        }

        /// <summary>
        /// Applies the current filter to the datasource of the datagridview
        /// </summary>
        /// <param name="force">If true, update the filter regardless if it was the same on the datasource</param>
        private void ApplyFilter(bool force)
        {
            // if the grid has a datasource
            var bs = grid.DataSource as SortableBindingList<WordEntry>;
            if (bs != null)
            {
                // check if the list should only show errors and warnings
                string targetFilter = showOnlyErrorsToolStripMenuItem.Checked ? "NeedsWork = true" : "";
                if (!string.IsNullOrEmpty(txtFilter.Text))
                {
                    // append with the additional criteria to match the text in the filter if it isn't empty
                    if (!string.IsNullOrEmpty(targetFilter))
                        targetFilter += " AND ";
                    targetFilter += "Text %LIKE% '" + txtFilter.Text.Replace("'", @"\'") + "'";
                }

                // udpate the filter property on the binding list
                if (force && bs.Filter == targetFilter)
                    bs.RemoveFilter();
                bs.Filter = targetFilter;

            }
            CheckEditMenuItemAvailibility();
        }

        /// <summary>
        /// Occurs when the selection is changed of the grid
        /// Update the occurence listbox with
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void grid_SelectionChanged(object sender, EventArgs e)
        {
            // if a row is selected
            if (grid.CurrentRow == null)
                return;

            // and the row has a wordentry databound item
            var we = grid.CurrentRow.DataBoundItem as WordEntry;
            if (we != null && (WordEntry)lstOccurrences.Tag != we)
            {
                // add all occurences to the list
                lstOccurrences.Tag = we;
                try
                {
                    lstOccurrences.BeginUpdate();
                    lstOccurrences.Items.Clear();
                    lstOccurrences.Items.AddRange(we.Occurrences.Cast<object>().ToArray());
                }
                finally
                {
                    lstOccurrences.EndUpdate();
                }
            }
        }

        /// <summary>
        /// Occurs when the edit control is shown in the datagridview.
        /// Used to fill the dictionary suggestions in the dropdown
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void grid_EditingControlShowing(object sender, DataGridViewEditingControlShowingEventArgs e)
        {
            if (grid.CurrentRow == null)
                return;

            //If the control is an autocomplete text editing control, fill the dropdown with the suggestions of the matching word entry of the row
            if (e.Control is AutocompleteTextEditingControl)
            {
                var ddl = (AutocompleteTextEditingControl)e.Control;
                ddl.Items.Clear();
                var we = grid.CurrentRow.DataBoundItem as WordEntry;
                if (we != null && we.DictionarySuggesions != null)
                    ddl.Items.AddRange(we.DictionarySuggesions);
            }
        }

        /// <summary>
        /// Occurs when a cell is clicked. Used to start editing as soon as the user clicks the grid
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void grid_CellClick(object sender, DataGridViewCellEventArgs e)
        {
            grid.BeginEdit(true);
        }

        /// <summary>
        /// Occurs when the content of a cell is clicked, used for the actions behind the icons
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void grid_CellContentClick(object sender, DataGridViewCellEventArgs e)
        {
            // if the user might be selecting multiple rows using Shift or deselecting using Control
            if (Control.ModifierKeys.HasFlag(Keys.Shift) || Control.ModifierKeys.HasFlag(Keys.Control))
                return;

            // if the row is valid
            if (e.RowIndex < 0 || e.RowIndex >= grid.RowCount && grid.Rows[e.RowIndex] == null)
                return;

            // Since we disallow Ctrl / Shift + Click the selection will always be reset to the row which got clicked
            int[] selectedRow = new int[] { e.RowIndex };

            if (grid.Columns[e.ColumnIndex].Name == "Ignore")
            {
                // Grab ignore flag status from clicked entry, then use the inverse value
                var we = grid.Rows[e.RowIndex].DataBoundItem as WordEntry;
                if (we == null)
                    return;
                bool originalIgnoreValue = we.Ignore;
                IgnoreEntry(selectedRow, !originalIgnoreValue);
            }
            else if (grid.Columns[e.ColumnIndex].Name == "AddToDictionary")
            {
                AddToDictionaryAndIgnoreEntry(selectedRow);
            }
            else if (grid.Columns[e.ColumnIndex].Name == "Copy")
            {
                UseSuggestionForEntry(selectedRow);
            }
        }

        /// <summary>
        /// Occurs when the edit is complete. Used to update the statistics and removing the ignore from the word entry
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void grid_CellEndEdit(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= grid.RowCount && grid.Rows[e.RowIndex] == null)
                return;
            var we = grid.Rows[e.RowIndex].DataBoundItem as WordEntry;
            if (we == null)
                return;

            // remove ignore if there is fixed text present
            if (!string.IsNullOrEmpty(we.FixedText))
                we.Ignore = false;

            // update the statistics
            UpdateStatistics();
        }

        /// <summary>
        /// Occurs when the row is about to be painted. Used to color code the row based on the state of the word entry
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void grid_RowPrePaint(object sender, DataGridViewRowPrePaintEventArgs e)
        {
            // if the row is valid
            if (e.RowIndex < 0 || e.RowIndex >= grid.RowCount && grid.Rows[e.RowIndex] == null)
                return;
            // and it has a word entry bound
            var we = grid.Rows[e.RowIndex].DataBoundItem as WordEntry;
            if (we == null)
                return;

            if (!we.Ignore && !string.IsNullOrEmpty(we.FixedText))
                // make the row white when the fixed text is filled in
                grid.Rows[e.RowIndex].DefaultCellStyle.BackColor = Color.White;
            else
            {
                if (!we.IsUnknownWord)
                {
                    // if it a word present in the dictionary
                    if (!we.IsWarning)
                        // show it as green if it's not a warning
                        grid.Rows[e.RowIndex].DefaultCellStyle.BackColor = Color.LightGreen;
                    else
                        // otherwise light orange
                        grid.Rows[e.RowIndex].DefaultCellStyle.BackColor = Color.FromArgb(255, 200, 100);
                }
                else
                {
                    // if it's not a word present in the dictionary but ignored, show the row as light gray
                    if (we.Ignore)
                        grid.Rows[e.RowIndex].DefaultCellStyle.BackColor = Color.LightGray;
                    else
                        grid.Rows[e.RowIndex].DefaultCellStyle.BackColor = Color.White;
                }
            }
        }

        /// <summary>
        /// Updates the statistics of the word entry list
        /// </summary>
        private void UpdateStatistics()
        {
            var lst = grid.DataSource as SortableBindingList<WordEntry>;
            if (lst != null)
            {
                int count = 0;
                int suggestionCount = 0;
                int fixedCount = 0;
                int unknownCount = 0;
                int ignoredCount = 0;
                int warningCount = 0;

                int occCount = 0;
                int occSuggestionCount = 0;
                int occFixedCount = 0;
                int occUnknownCount = 0;
                int occIgnoredCount = 0;
                int occWarningCount = 0;
                // use the full list of the word entries
                foreach (var we in lst.OriginalList)
                {
                    count++;
                    occCount += we.Count;

                    if (we.IsUnknownWord)
                    {
                        unknownCount++;
                        occUnknownCount += we.Count;
                    }

                    if (we.IsUnknownWord && !string.IsNullOrEmpty(we.Suggestion))
                    {
                        suggestionCount++;
                        occSuggestionCount += we.Count;
                    }

                    if (!string.IsNullOrEmpty(we.FixedText))
                    {
                        fixedCount++;
                        occFixedCount += we.Occurrences.Where(occ => !occ.Ignore).Count();
                    }

                    if (we.IsUnknownWord && we.Ignore)
                    {
                        ignoredCount++;
                        occIgnoredCount += we.Count;
                    }

                    if (we.IsWarning)
                    {
                        warningCount++;
                        occWarningCount += we.Count;
                    }
                }

                // update the labels
                lblWordCount.Text = "Words: " + count + " [" + occCount + "]";
                lblUnknown.Text = "Unknown: " + unknownCount + " [" + occUnknownCount + "]";
                lblSuggestion.Text = "Suggestion: " + suggestionCount + " [" + occSuggestionCount + "]";
                lblFixed.Text = "Fixed: " + fixedCount + " [" + occFixedCount + "]";
                lblIgnored.Text = "Ignored: " + ignoredCount + " [" + occIgnoredCount + "]";
                lblWarning.Text = "Warning: " + warningCount + " [" + occWarningCount + "]";
            }
        }


        /// <summary>
        /// Occurs when a key is pressed while the data grid view is focussed. Used to bind some hotkeys.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void grid_KeyDown(object sender, KeyEventArgs e)
        {
            if (grid.CurrentCell != null)
            {
                // if space is pressed on one of the 3 icons -> execute the action of that icon
                if (e.KeyCode == Keys.Space)
                {
                    if (grid.CurrentCell.OwningColumn.Name == "Ignore")
                    {
                        // Grab ignore flag status from last selected entry, then use the inverse value
                        // for the entire selection
                        var we = grid.Rows[grid.CurrentCell.RowIndex].DataBoundItem as WordEntry;
                        if (we == null)
                            return;
                        bool originalIgnoreValue = we.Ignore;
                        IgnoreEntry(GetSelectedRowIndexes(), !originalIgnoreValue);
                    }
                    else if (grid.CurrentCell.OwningColumn.Name == "AddToDictionary")
                    {
                        AddToDictionaryAndIgnoreEntry(GetSelectedRowIndexes());
                    }
                    else if (grid.CurrentCell.OwningColumn.Name == "Copy")
                    {
                        UseSuggestionForEntry(GetSelectedRowIndexes());
                    }

                    e.Handled = true;
                }
                // if Control-A is pressed with selection but not when editing -> add to dictionary
                else if (e.Control && e.KeyCode == Keys.A)
                {
                    AddToDictionaryAndIgnoreEntry(GetSelectedRowIndexes());
                    e.Handled = true;
                }
                // if Control-Z is pressed on any cell of the row -> copy original to fixed text and start editing
                else if (e.Control && e.KeyCode == Keys.Z)
                {
                    CopyOriginalAndEditFixedCell(grid.CurrentCell.RowIndex);
                    e.Handled = true;
                }
                else if (e.Control && e.KeyCode == Keys.C)
                {
                    CopyToClipboard(grid.CurrentCell);
                }
                // if enter is pressed on an editable column -> start editing
                else if (e.KeyCode == Keys.Enter)
                {
                    if (!grid.CurrentCell.OwningColumn.ReadOnly)
                    {
                        // start editing
                        grid.BeginEdit(false);
                        e.Handled = true;
                    }
                }
            }
        }

        private void IgnoreEntry(int[] rowIndexes, bool ignoredFlag)
        {
            // Reset cell editing before we start replacing cell contents
            grid.CancelEdit();
            grid.EndEdit();

            foreach (var rowIndex in rowIndexes)
            {
                // find bound word entry or return if none available
                var we = grid.Rows[rowIndex].DataBoundItem as WordEntry;
                if (we == null)
                    continue;

                if (we.IsUserAdded)
                {
                    UndoAddToDictionary(we);
                }

                // toggle ignore of the word entry and redraw the row
                we.Ignore = ignoredFlag;
                grid.InvalidateRow(rowIndex);
            }
            UpdateStatistics();
        }

        private void UseSuggestionForEntry(int[] rowIndexes)
        {
            // Reset cell editing before we start replacing cell contents
            grid.EndEdit();

            foreach (var rowIndex in rowIndexes)
            {
                // find bound word entry or return if none available
                var we = grid.Rows[rowIndex].DataBoundItem as WordEntry;
                if (we == null)
                    return;

                if (we.IsUserAdded)
                {
                    UndoAddToDictionary(we);
                }

                // copy either the suggestion or the text if the suggestion is empty to the fixed text column for the current row
                if (string.IsNullOrEmpty(we.Suggestion))
                    we.FixedText = we.Text;
                else
                    we.FixedText = we.Suggestion;

                we.Ignore = false;

                // redraw the row
                grid.InvalidateRow(rowIndex);
            }
            UpdateStatistics();
        }

        private void UndoAddToDictionary(WordEntry we)
        {
            manager.RemoveFromDictionary(we.Text);
            we.IsUserAdded = false;
            we.IsUnknownWord = true;
        }

        private void AddToDictionaryAndIgnoreEntry(int[] rowIndexes)
        {
            // Reset cell editing before we start replacing cell contents
            grid.EndEdit();

            foreach (var rowIndex in rowIndexes)
            {
                // find bound word entry or return if none available
                var we = grid.Rows[rowIndex].DataBoundItem as WordEntry;
                if (we == null)
                    continue;

                // add the word to the custom dictionary (if already present or something matching a rule we do nothing)
                if (!we.IsUnknownWord)
                    continue;
                var isNewWord = manager.AddToDictionary(we.Text);
                if (!isNewWord)
                    continue;

                we.IsUnknownWord = false;
                we.IsUserAdded = true;
                // Make sure the entry can be colored green and colors gray if ignored later on
                we.FixedText = "";
                we.Ignore = false;

                //redraw the row
                grid.InvalidateRow(rowIndex);
            }
            UpdateStatistics();
        }

        private void CopyToClipboard(DataGridViewCell cell)
        {
            try
            {
                Clipboard.SetText(cell.Value + "");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Unable to copy the cell content to clipboard: " + ex.GetType().FullName + " - " + ex.Message);
            }
        }

        private void CopyOriginalAndEditFixedCell(int rowIndex)
        {
            // Reset value if currently editing
            grid.EndEdit();

            // if the row is valid
            if (rowIndex < 0 || rowIndex >= grid.RowCount && grid.Rows[rowIndex] == null)
                return;

            var currentRow = grid.Rows[rowIndex];

            // and there is a word entry bound
            var we = currentRow.DataBoundItem as WordEntry;
            if (we == null)
                return;

            if (we.IsUserAdded)
            {
                UndoAddToDictionary(we);
            }

            we.FixedText = we.Text;

            we.Ignore = false;

            int fixedTextIndex = grid.Columns.IndexOf(FixedText);

            grid.CurrentCell = currentRow.Cells[fixedTextIndex];
            grid.BeginEdit(true);

            // redraw the row
            grid.InvalidateRow(rowIndex);
            UpdateStatistics();
        }

        /// <summary>
        /// Occurs when the open epub menu item is chosen. Shows the open file dialog to select an epub and loads the epub.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void openToolStripMenuItem_Click(object sender, EventArgs e)
        {
            try
            {
                using (OpenFileDialog ofd = new OpenFileDialog())
                {
                    // show only epub files
                    ofd.Filter = "*.epub|*.epub";
                    if (ofd.ShowDialog(this) == System.Windows.Forms.DialogResult.OK)
                    {
                        // load the epub
                        string path = ofd.FileName;
                        OpenEpub(path);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "The file could not be opened: " + ex.GetType().FullName + " - " + ex.Message, "File could not be opened", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }


        /// <summary>
        /// Occurs when the show only errors menu item is toggled
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void showOnlyErrorsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            ApplyFilter(false);
        }

        /// <summary>
        /// Occurs when the copy all suggestions menu item is clicked. Used to copy the suggestion to the fixed text for each entry
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void copyAllSuggestionsToFixedTextToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var lst = grid.DataSource as SortableBindingList<WordEntry>;
            if (lst != null)
            {
                foreach (var we in lst.OriginalList)
                {
                    // if it's an unknown word and there is a suggestion, copy the suggestion to the fixed text
                    if (we.IsUnknownWord && !string.IsNullOrEmpty(we.Suggestion))
                        we.FixedText = we.Suggestion;
                }
            }
        }

        /// <summary>
        /// Occurs when the save epub menu item is chosen. Show the save dialog to save the epub
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void saveToolStripMenuItem_Click(object sender, EventArgs e)
        {
            try
            {
                var lst = grid.DataSource as SortableBindingList<WordEntry>;
                if (lst != null)
                {
                    using (SaveFileDialog sfd = new SaveFileDialog())
                    {
                        sfd.Filter = "*.epub|*.epub";
                        if (sfd.ShowDialog(this) == System.Windows.Forms.DialogResult.OK)
                        {

                            // create a clone of the current epub to prevent changing character offsets when applying
                            // the corrected text
                            using (var newEpub = currentEpub.Clone())
                            {
                                // apply all the corrections on the clone
                                manager.Apply(newEpub, lst.OriginalList);

                                // save the clone to the chosen file
                                newEpub.Save(sfd.FileName);

                                MessageBox.Show(this, "The changes have been saved succesfully", "Save succesful", MessageBoxButtons.OK, MessageBoxIcon.Information);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "The file could not be saved: " + ex.GetType().FullName + " - " + ex.Message, "File could not be saved", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// Occurs when the recheck ocr patterns menu item is chosen. Reload the OCR pattern file and recheck all word entries.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void recheckOCRPatternsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var lst = grid.DataSource as SortableBindingList<WordEntry>;
            if (lst != null)
            {
                // reload the OCR patterns file and recheck all word entries for OCR errors
                manager.ReloadOCRPatterns(lst.OriginalList);
            }
        }

        /// <summary>
        /// Occurs when the exit menu item is chosen. Ask to exit and close the form if confirmed.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void exitToolStripMenuItem_Click(object sender, EventArgs e)
        {
            this.Close();
        }

        /// <summary>
        /// Occurs when the text is changed in the filter. Reset the filter timer.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void txtFilter_TextChanged(object sender, EventArgs e)
        {
            // reset the filter timer to prevent lag spikes while typing.
            tmrFilter.Enabled = false;
            tmrFilter.Enabled = true;
        }

        /// <summary>
        /// Occurs when the delay of the filter has passed. Applies the filter on the current datasource of the data grid view
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void tmrFilter_Tick(object sender, EventArgs e)
        {
            tmrFilter.Enabled = false;
            var lst = grid.DataSource as SortableBindingList<WordEntry>;
            if (lst != null)
            {
                // apply the filter
                ApplyFilter(false);
            }
        }


        /// <summary>
        /// Occurs when the form is closed. Save the custom dictionary.
        /// </summary>
        /// <param name="e"></param>
        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            manager.SaveCustomDictionary();
        }


        /// <summary>
        /// Occurs when an item in the occurence listbox needs to be drawn. Draws a word entry with text before and after the word, with the word itself in bold
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void lstOccurrences_DrawItem(object sender, DrawItemEventArgs e)
        {
            // if the item is in a valid range
            if (e.Index >= 0 && e.Index < lstOccurrences.Items.Count)
            {
                using (Bitmap bmp = new Bitmap(e.Bounds.Width, e.Bounds.Height))
                {
                    Graphics g = Graphics.FromImage(bmp);

                    Word w = (Word)lstOccurrences.Items[e.Index];

                    // draw the selection if the item is selected
                    if ((e.State & DrawItemState.Selected) == DrawItemState.Selected)
                        g.Clear(Color.SkyBlue);
                    //e.Graphics.FillRectangle(Brushes.SkyBlue, e.Bounds);
                    else
                    {
                        // draw ignore as light gray background
                        if (w.Ignore)
                            g.Clear(Color.LightGray);
                            //e.Graphics.FillRectangle(Brushes.LightGray, e.Bounds);
                        else
                            g.Clear(Color.White);
                            //e.Graphics.FillRectangle(Brushes.White, e.Bounds);
                    }

                    int displayLength = 100;
                    var surroundingText = GetContextOfWord(w, displayLength);

                    using (var boldFont = new Font(lstOccurrences.Font, FontStyle.Bold))
                    {
                        // measure the sizes of the parts


                        var textSize = e.Graphics.MeasureString(w.Text, boldFont);
                        var prefixSize = e.Graphics.MeasureString(surroundingText.Prefix, lstOccurrences.Font);
                        var postfixSize = e.Graphics.MeasureString(surroundingText.Postfix, lstOccurrences.Font);

                        // draw the strings
                        using (SolidBrush br = new SolidBrush(lstOccurrences.ForeColor))
                        {
                            float left = 0;

                            g.DrawString(surroundingText.Prefix, lstOccurrences.Font, br, new RectangleF(left, 0, prefixSize.Width, prefixSize.Height));
                            left += prefixSize.Width;
                            g.DrawString(w.Text, boldFont, br, new RectangleF(left, 0, textSize.Width, textSize.Height));
                            left += textSize.Width;
                            g.DrawString(surroundingText.Postfix, lstOccurrences.Font, br, new RectangleF(left, 0, postfixSize.Width, postfixSize.Height));
                        }
                    }

                    e.Graphics.DrawImage(bmp, new Point(e.Bounds.Left, e.Bounds.Top));
                }
            }
        }

        private SurroundingText GetContextOfWord(Word w, int displayLength)
        {
            var textEntry = currentEpub.Entries[w.Href] as Epub.HtmlEntry;

            // determine the max length of the text before and after the word
            int min = Math.Max(w.CharOffset - displayLength, 0);
            int length = w.CharOffset + w.Text.Length + displayLength > textEntry.Html.Length ? textEntry.Html.Length - (w.CharOffset + w.Text.Length) : displayLength;

            // build the text before and after the text
            string prefix = "..." + textEntry.Html.Substring(min, displayLength).Replace("\r", "").Replace("\n", "");
            // todo: this won't correctly display if the text is broken into pieces with tags inbetween. Don't use w.Text.Length, but use the last char offset instead
            string postfix = textEntry.Html.Substring(w.OriginalCharPositions.Last() + 1, length).Replace("\r", "").Replace("\n", "") + "...";

            return new SurroundingText() { Prefix = prefix, Postfix = postfix, Word = w };
        }

        private class SurroundingText
        {
            public string Prefix { get; set; }
            public Word Word { get; set; }
            public string Postfix { get; set; }

            public override string ToString()
            {
                return Prefix + Word.Text + Postfix;
            }
        }

        /// <summary>
        /// Occurs when the ignore lines button is clicked. Toggle all ignore states of the current selected lines
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void btnIgnoreLines_Click(object sender, EventArgs e)
        {
            foreach (var itm in lstOccurrences.SelectedItems.Cast<Word>())
            {
                itm.Ignore = !itm.Ignore;
            }
            lstOccurrences.Invalidate();
            UpdateStatistics();
        }

        /// <summary>
        /// Occurs when the word distribution analysis menu item is clicked. Show the analysis form with the current epub and word entries.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void wordDistributionAnalysisToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var lst = grid.DataSource as SortableBindingList<WordEntry>;
            if (lst != null)
            {
                using (WordDistributionAnalysis dlg = new WordDistributionAnalysis(currentEpub, lst))
                {
                    dlg.ShowDialog(this);
                }
            }
        }

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            // dispose the loader
            if (disposing && loader != null)
            {
                loader.Dispose();
            }
            base.Dispose(disposing);
        }

        /// <summary>
        /// Occurs when the about menu is clicked, shows the about box
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void aboutToolStripMenuItem_Click(object sender, EventArgs e)
        {
            using (AboutBox dlg = new AboutBox())
            {
                dlg.ShowDialog(this);
            }
        }

        /// <summary>
        /// Occurs when a key is pressed in the occurence listbox
        /// Toggle ignore with space
        /// Copy text to clipboard with ctrl+c
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void lstOccurrences_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space)
            {
                btnIgnoreLines_Click(btnIgnoreLines, EventArgs.Empty);
                e.Handled = true;
            }
            else if (e.Control && e.KeyCode == Keys.C)
            {
                try
                {
                    var word = lstOccurrences.SelectedItem as Word;
                    if (word != null)
                        Clipboard.SetText(GetContextOfWord(word, 100).ToString());
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Unable to copy text to clipboard: " + ex.GetType().FullName + " - " + ex.Message);
                }
            }
        }

        /// <summary>
        /// Occurs when the preferences menu is clicked, shows the preferences dialog
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void preferencesToolStripMenuItem_Click(object sender, EventArgs e)
        {
            try
            {
                using (PreferencesDialog dlg = new PreferencesDialog())
                {
                    dlg.ShowDialog(this);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error opening preferences: " + ex.GetType().FullName + " - " + ex.Message + Environment.NewLine + ex.StackTrace);
            }
        }

        private void ignoreToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (grid.CurrentCell != null)
            {
                // Grab ignore flag status from last selected entry, then use the inverse value
                // for the entire selection
                var we = grid.Rows[grid.CurrentCell.RowIndex].DataBoundItem as WordEntry;
                if (we == null)
                    return;
                bool originalIgnoreValue = we.Ignore;
                IgnoreEntry(GetSelectedRowIndexes(), !originalIgnoreValue);
            }
        }

        private void editOriginalValueToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (grid.CurrentCell != null)
            {
                CopyOriginalAndEditFixedCell(grid.CurrentCell.RowIndex);
            }
        }

        private void copyCellValueToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (grid.CurrentCell != null)
            {
                CopyToClipboard(grid.CurrentCell);
            }
        }

        private void addToDictionaryToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (grid.CurrentCell != null)
            {
                int[] selectedRows = GetSelectedRowIndexes();
                AddToDictionaryAndIgnoreEntry(selectedRows);
            }
        }

        private void useSuggestionToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (grid.CurrentCell != null)
            {
                UseSuggestionForEntry(GetSelectedRowIndexes());
            }
        }

        /// <summary>
        /// Enables or disables the items in the edit menu depending on whether any items are displayed in the grid.
        /// </summary>
        private void CheckEditMenuItemAvailibility()
        {
            bool enableEditMenu = grid.CurrentCell != null;

            copyCellValueToolStripMenuItem.Enabled = enableEditMenu;
            ignoreToolStripMenuItem.Enabled = enableEditMenu;
            addToDictionaryToolStripMenuItem.Enabled = enableEditMenu;
            useSuggestionToolStripMenuItem.Enabled = enableEditMenu;
            editOriginalValueToolStripMenuItem.Enabled = enableEditMenu;
        }

        /// <summary>
        /// Returns the row indexes for all the cells which the user selected.
        /// </summary>
        /// <returns></returns>
        private int[] GetSelectedRowIndexes()
        {
            return grid.SelectedCells.Cast<DataGridViewCell>()
                .Select(el => el.RowIndex)
                // The grid allows multiple columns to be selected so filter cells whose row index
                // is one which we already encountered earlier.
                .Distinct()
                .ToArray();
        }

        /// <summary>
        /// Asks to save changes before closing, at least when an Epub file has been opened.
        /// </summary>
        protected override void OnClosing(CancelEventArgs e)
        {
            if (currentEpub == null)
                return;

            var result = MessageBox.Show(this, "Are you sure you want to exit, all unsaved changes will be lost?", "Are you sure?", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            e.Cancel = result == System.Windows.Forms.DialogResult.No;
        }
    }
}
