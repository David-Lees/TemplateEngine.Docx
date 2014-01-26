﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using DocumentFormat.OpenXml.Packaging;

namespace TemplateEngine.Docx
{
    public class TemplateProcessor : IDisposable
    {
        public readonly XDocument Document;
        private readonly WordprocessingDocument _wordDocument;
	    private bool _isNeedToRemoveContentControls = false;

        public TemplateProcessor(string fileName)
        {
            _wordDocument = WordprocessingDocument.Open(fileName, true);

            var xdoc = _wordDocument.MainDocumentPart.Annotation<XDocument>();
            if (xdoc == null)
            {
                using (Stream str = _wordDocument.MainDocumentPart.GetStream())
                using (var streamReader = new StreamReader(str))
                using (XmlReader xr = XmlReader.Create(streamReader))
                    xdoc = XDocument.Load(xr);

                _wordDocument.MainDocumentPart.AddAnnotation(xdoc);
            }
            
            Document = xdoc;
        }

	    public TemplateProcessor SetRemoveContentControls(bool isNeedToRemove)
	    {
		    _isNeedToRemoveContentControls = isNeedToRemove;
		    return this;
	    }

        public void SaveChanges()
        {
            if (Document == null) return;

            // Serialize the XDocument object back to the package.
            using (XmlWriter xw = XmlWriter.Create(_wordDocument.MainDocumentPart.GetStream (FileMode.Create, FileAccess.Write)))
            {
                Document.Save(xw);
            }

            _wordDocument.Close();
        }

        public TemplateProcessor(XDocument templateSource)
        {
            this.Document = templateSource;
        }

        public TemplateProcessor FillContent(Content content)
        {
            var errors = new List<string>();

            // Filling a fields
            if (content.Fields != null)
            {
                foreach (var field in content.Fields)
                {
                    var fieldsContentControl = Document.Root
	                    .Element(W.body)
	                    .Descendants(W.sdt)
                        .Where(sdt => field.Name == sdt.Element(W.sdtPr).Element(W.tag).Attribute(W.val).Value);

                    // If there isn't a field with that name, add an error to the error string,
                    // and continue with next field.
                    if (fieldsContentControl == null)
                    {
                        errors.Add(String.Format("Field Content Control '{0}' not found.",
                            field.Name));
                        continue;
                    }

                    // Set content control value to the new value
                    foreach (var fieldContentControl in fieldsContentControl)
                    {
                        fieldContentControl
                            .Element(W.sdtContent)
                            .Descendants(W.t)
                            .FirstOrDefault()
                            .Value = field.Value;
                            if (_isNeedToRemoveContentControls)
	                    {
			        // Remove the content control for the table and replace it with its contents.
				XElement replacementElement =
				    new XElement(fieldContentControl.Element(W.sdtContent).Elements().First());
				fieldContentControl.ReplaceWith(replacementElement);
	                    }
                    }
	                
                }
            }
            
            // Filling a tables
            if (content.Tables != null)
            {
                foreach (var table in content.Tables)
                {
                    // Find the content control with Table Name
                    var listName = table.Name;
                    var tableContentControl = Document.Root
	                    .Element(W.body)
	                    .Elements(W.sdt)
						.FirstOrDefault(sdt => listName == sdt.Element(W.sdtPr).Element(W.tag).Attribute(W.val).Value);

                    // If there isn't a table with that name, add an error to the error string,
                    // and continue with next table.
                    if (tableContentControl == null)
                    {
                        errors.Add(String.Format("Table Content Control '{0}' not found.",
                            listName));
                        continue;
                    }

                    // If the table doesn't contain content controls in cells, then error and continue with next table.
                    var cellContentControl = tableContentControl
                        .Descendants(W.sdt)
                        .FirstOrDefault();
                    if (cellContentControl == null)
                    {
                        errors.Add(String.Format(
                            "Table Content Control '{0}' doesn't contain content controls in cells.",
                            listName));
                        continue;
                    }

                    // Determine the element for the row that contains the content controls.
                    // This is the prototype for the rows that the code will generate from data.
                    var prototypeRow = tableContentControl
                        .Descendants(W.sdt)
                        .Ancestors(W.tr)
                        .FirstOrDefault();

                    // Create a list of new rows to be inserted into the document.  Because this
                    // is a document centric transform, this is written in a non-functional
                    // style, using tree modification.
                    var newRows = new List<XElement>();
                    foreach (var row in table.Rows)
                    {
                        // Clone the prototypeRow into newRow.
                        var newRow = new XElement(prototypeRow);

                        // Create new rows that will contain the data that was passed in to this
                        // method in the XML tree.
                        foreach (var sdt in newRow.Descendants(W.sdt).ToList())
                        {
                            // Get fieldName from the content control tag.
                            string fieldName = sdt
                                .Element(W.sdtPr)
                                .Element(W.tag)
                                .Attribute(W.val)
                                .Value;

                            // Get the new value out of contentControlValues.
                            var newValueElement = row
                                .Fields
                                .Where(f => f.Name == fieldName)
                                .FirstOrDefault();

                            // Generate error message if the new value doesn't exist.
                            if (newValueElement == null)
                            {
                                errors.Add(String.Format(
                                    "Table '{0}', Field '{1}' value isn't specified.",
                                    listName, fieldName));
                                continue;
                            }

                            // Set content control value th the new value
							sdt.Element(W.sdtContent)
								.Descendants(W.t)
								.FirstOrDefault()
								.Value = newValueElement.Value;

	                        if (_isNeedToRemoveContentControls == true)
	                        {
								// Remove the content control, and replace it with its contents.
								XElement replacementElement =
									new XElement(sdt.Element(W.sdtContent).Elements().First());
								sdt.ReplaceWith(replacementElement);
	                        }
                        }

                        // Add the newRow to the list of rows that will be placed in the newly
                        // generated table.
                        newRows.Add(newRow);
                    }

                    prototypeRow.AddAfterSelf(newRows);

	                if (_isNeedToRemoveContentControls == true)
	                {
						// Remove the content control for the table and replace it with its contents.
						XElement tableElement = prototypeRow.Ancestors(W.tbl).First();
						var tableClone = new XElement(tableElement);
						tableContentControl.ReplaceWith(tableClone);
	                }

					// Remove the prototype row and add all of the newly constructed rows.
					prototypeRow.Remove(); 
                }
            }

            // Add any errors as red text on yellow at the beginning of the document.
            if (errors.Any())
                Document.Root
                    .Element(W.body)
                    .AddFirst(errors.Select(s =>
                        new XElement(W.p,
                            new XElement(W.r,
                                new XElement(W.rPr,
                                    new XElement(W.color,
                                        new XAttribute(W.val, "red")),
                                    new XElement(W.sz,
                                        new XAttribute(W.val, "28")),
                                    new XElement(W.szCs,
                                        new XAttribute(W.val, "28")),
                                    new XElement(W.highlight,
                                        new XAttribute(W.val, "yellow"))),
                                new XElement(W.t, s)))));

            return this;
        }

        public void Dispose()
        {
            if (_wordDocument == null) return;

            _wordDocument.Dispose();
        }
    }
}
